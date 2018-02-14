﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster.Tools.PublishSubscribe;
using Akka.DistributedData;
using Akka.Event;
using Akka.Routing;
using Akka.Cluster.Routing;
using Akka.Configuration;
using System.Configuration;
using Akka.Configuration.Hocon;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hexagon.Akka.UnitTests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hexagon.Akka.MultiNodeTests")]

namespace Hexagon.AkkaImpl
{
    public class MessageSystem<M, P> : IDisposable
        where P : IMessagePattern<M>
        where M : IMessage
    {
        readonly ActorSystem ActorSystem;
        public IActorRef Mediator => DistributedPubSub.Get(ActorSystem).Mediator;
        public IActorRef Replicator => DistributedData.Get(ActorSystem).Replicator;
        public readonly IMessageFactory<M> MessageFactory;
        public readonly IMessagePatternFactory<P> PatternFactory;
        readonly ILoggingAdapter Logger;
        public readonly NodeConfig NodeConfig;
        readonly ActorDirectory<M,P> ActorDirectory;
        const string cNodeRoleName = "_node_";

        static MessageSystem<M, P> _instance = null;
        public static MessageSystem<M, P> Instance
        {
            get => _instance;
            private set
            {
                if (_instance != null)
                    throw new Exception($"MessageSystem<{typeof(M).Name},{typeof(P).Name}> singleton already set !");
                _instance = value;
            }
        }

        public MessageSystem(
            string systemName, 
            IMessageFactory<M> messageFactory, 
            IMessagePatternFactory<P> patternFactory,
            NodeConfig nodeConfig)
            : this(
                  ActorSystem.Create(
                      systemName,
                      ConfigurationFactory
                      .ParseString($@"akka.cluster.roles = [{string.Join(",", nodeConfig.Roles.Union(new[] { cNodeRoleName }).Distinct())}]")
                      .WithFallback(DefaultConfig())),
                  messageFactory, 
                  patternFactory,
                  nodeConfig)
        {
        }

        public MessageSystem(
            ActorSystem system, 
            IMessageFactory<M> factory,
            IMessagePatternFactory<P> patternFactory,
            NodeConfig nodeConfig)
        {
            ActorSystem = system;
            MessageFactory = factory;
            PatternFactory = patternFactory;
            Logger = Logging.GetLogger(system, this);
            NodeConfig = nodeConfig;
            ActorDirectory = new ActorDirectory<M, P>(system, nodeConfig);

            Instance = this;
        }

        public static Config DefaultConfig()
        {
            var section = (AkkaConfigurationSection)ConfigurationManager.GetSection("akka");
            return
                section.AkkaConfig
                .WithFallback(ConfigurationFactory.ParseString($@"
                    akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider, Akka.Cluster""
                    akka.remote.dot-netty.tcp.hostname = ""{System.Net.Dns.GetHostName()}""
                    akka.remote.dot-netty.tcp.port = 0
                    akka.cluster.pub-sub.role = {cNodeRoleName}
                    akka.cluster.distributed-data.role = {cNodeRoleName}
                    akka.cluster.distributed-data.pruning-interval = 0    # temporary (pruning bug)
                "))
                .WithFallback(DistributedData.DefaultConfig())
                .WithFallback(DistributedPubSub.DefaultConfig());
        }

        public async void SendMessage(M message, ICanReceiveMessage<M> sender)
        {
            if (sender != null && !(sender is ActorRefMessageReceiver<M>))
                throw new ArgumentException("the sender must be a ActorRefMessageReceiver", "sender");

            var actorPaths = await ActorDirectory.GetMatchingActors(message, PatternFactory);
            if (!actorPaths.Any())
            {
                Logger.Error(@"Cannot find any receiver of message {0}", message);
                return;
            }
            var primaryReceivers = actorPaths.Where(ma => !ma.IsSecondary);
            if (primaryReceivers.Any())
            {
                if (Logger.IsDebugEnabled)
                    Logger.Debug(@"Primary receiver(s) of message {0} : {1}", message, string.Join(", ", primaryReceivers.Select(ma => ma.Path)));
                string mainReceiverPath = GetMainReceiverPath(primaryReceivers);
                if (Logger.IsDebugEnabled)
                    Logger.Debug(@"Selected primary receiver of message {0} : {1}", message, mainReceiverPath);
                var mainReceiver = new ActorPathMessageReceiver<M>(mainReceiverPath, Mediator);
                mainReceiver.Tell(message, sender);
            }
            else
            {
                Logger.Warning(@"No primary receiver found for message {0}", message);
            }
            var secondaryReceiverPaths = actorPaths.Where(ma => ma.IsSecondary).Select(ma => ma.Path);
            if (secondaryReceiverPaths.Any())
            {
                if (Logger.IsDebugEnabled)
                    Logger.Debug(@"Secondary receiver(s) of message {0} : {1}", message, string.Join(", ", secondaryReceiverPaths));
                var readonlySender = sender == null ? null : new ReadOnlyActorRefMessageReceiver<M>(sender as ActorRefMessageReceiver<M>);
                foreach (var secondaryActorPath in secondaryReceiverPaths)
                {
                    var secondaryReceiver = new ActorPathMessageReceiver<M>(secondaryActorPath, Mediator);
                    secondaryReceiver.Tell(message, readonlySender);
                }
            }
        }

        string GetMainReceiverPath(IEnumerable<ActorDirectory<M,P>.MatchingActor> primaryActors)
        {
            System.Diagnostics.Debug.Assert(primaryActors.All(actor => actor.IsSecondary == false));

            // Look for the primary actors with highest matching score
            var primaryActorsOrderdedByMatchingScore = primaryActors.OrderByDescending(ma => ma.MatchingScore);
            int highestMatchingScore = primaryActorsOrderdedByMatchingScore.First().MatchingScore;
            var mainPrimaryActors = primaryActorsOrderdedByMatchingScore.TakeWhile(ma => ma.MatchingScore == highestMatchingScore);
            if (mainPrimaryActors.Count() == 1)
            {
                // If only one, return it
                var selectedActor = mainPrimaryActors.First();
                if (Logger.IsDebugEnabled && primaryActors.Count() > 1)
                    Logger.Debug($"Primary actor {selectedActor.Path} selected by highest matching score {highestMatchingScore}");
                return selectedActor.Path;
            }
            else
            {
                Logger.Warning($"Several actors with same highest matching score {highestMatchingScore}, randomly picking one based on actor mistrust factors...");
                // If several primary actors have the same highest score, randomly pick one depending on the mistrust factor
                var mistrusts = mainPrimaryActors.Select(ma => ma.MistrustFactor).ToArray();
                var selectedActor = mainPrimaryActors.ElementAt(MistrustBasedRandomRoutingLogic.SelectIndex(mistrusts));
                if (Logger.IsDebugEnabled)
                    Logger.Debug($"Primary actor {selectedActor.Path} randomly selected (mistrust factor : {selectedActor.MistrustFactor})");
                return selectedActor.Path;
            }
        }

        public async Task<M> SendMessageAndAwaitResponse(M message, ICanReceiveMessage<M> sender, TimeSpan? timeout = null, System.Threading.CancellationToken? cancellationToken = null)
        {
            if (sender != null && !(sender is ActorRefMessageReceiver<M>))
                throw new ArgumentException("the sender must be a ActorRefMessageReceiver", "sender");

            var actorPaths = await ActorDirectory.GetMatchingActors(message, PatternFactory);
            if (!actorPaths.Any())
            {
                Logger.Error(@"Cannot find any receiver of message {0}", message);
                return default(M);
            }
            var secondaryReceiverPaths = actorPaths.Where(ma => ma.IsSecondary).Select(ma => ma.Path);
            if (secondaryReceiverPaths.Any())
            {
                Logger.Debug(@"Secondary receivers of message {0} : {1}", message, string.Join(", ", secondaryReceiverPaths));
                var readonlySender = sender == null ? null : new ReadOnlyActorRefMessageReceiver<M>(sender as ActorRefMessageReceiver<M>);
                foreach (var secondaryActorPath in secondaryReceiverPaths)
                {
                    var secondaryReceiver = new ActorPathMessageReceiver<M>(secondaryActorPath, Mediator);
                    secondaryReceiver.Tell(message, readonlySender);
                }
            }
            var primaryReceivers = actorPaths.Where(ma => !ma.IsSecondary);
            if (primaryReceivers.Any())
            {
                if (Logger.IsDebugEnabled)
                    Logger.Debug(@"Primary receiver(s) of message {0} : {1}", message, string.Join(", ", primaryReceivers.Select(ma => ma.Path)));
                string mainReceiverPath = GetMainReceiverPath(primaryReceivers);
                var mainReceiver = new ActorPathMessageReceiver<M>(mainReceiverPath, Mediator);
                return await mainReceiver.Ask(message, MessageFactory, timeout ?? TimeSpan.FromSeconds(10), cancellationToken);
            }
            return default(M);
        }

        public void Start(PatternActionsRegistry<M,P> registry = null)
        {
            Logger.Info("Starting the message system...");
            // Initialize mediator
            DistributedPubSub.Get(ActorSystem);
            // Initialize replicator
            DistributedData.Get(ActorSystem);
            var actionsRegistry = new PatternActionsRegistry<M, P>();
            actionsRegistry.AddRegistry(registry);
            // Create actors from registry
            if (NodeConfig.Assemblies != null)
            {
                foreach (var assembly in NodeConfig.Assemblies)
                {
                    actionsRegistry.AddActionsFromAssembly(assembly);
                }
            }
            CreateActors(actionsRegistry).Wait();
            // Synchronize other actors
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(NodeConfig.GossipTimeFrameInSeconds));
            Logger.Info("Message system started and ready !");
        }

        static Func<PatternActionsRegistry<M, P>.MessageRegistryEntry, Predicate<M>> FilterEntry => entry => message => entry.Pattern.Match(message);
        static Func<PatternActionsRegistry<M, P>.MessageRegistryEntry, Predicate<M>> NoFilterEntry => entry => null;

        public static (IEnumerable<Actor<M,P>.ActionWithFilter>, IEnumerable<Actor<M, P>.AsyncActionWithFilter>) GetActions(IEnumerable<PatternActionsRegistry<M, P>.MessageRegistryEntry> registryEntries)
        {
            Func<PatternActionsRegistry<M, P>.MessageRegistryEntry, Predicate<M>> filter = (registryEntries.Count() == 1 ? NoFilterEntry : FilterEntry);
            var actions =
                registryEntries
                .Where(entry => entry.CodeType == EActionType.Code && entry.Action != null)
                .Select(entry =>
                    new Actor<M, P>.ActionWithFilter
                    {
                        Action = entry.Action,
                        Filter = filter(entry)
                    });
            var powershellActions =
                registryEntries
                .Where(entry => entry.CodeType == EActionType.PowershellScript)
                .Select(entry =>
                    new Actor<M, P>.ActionWithFilter
                    {
                        Action = PowershellScriptToAction(entry.Code, !entry.Pattern.IsSecondary),
                        Filter = filter(entry)
                    });
            var asyncActions =
                registryEntries
                .Where(entry => entry.CodeType == EActionType.Code && entry.AsyncAction != null)
                .Select(entry =>
                    new Actor<M, P>.AsyncActionWithFilter
                    {
                        Action = entry.AsyncAction,
                        Filter = filter(entry)
                    });
            return (actions.Union(powershellActions), asyncActions);
        }

        private async Task CreateActors(PatternActionsRegistry<M, P> registry)
        {
            var groups = registry.LookupByKey();
            foreach (var group in groups)
            {
                string actorName = group.Key;

                var props = NodeConfig.GetActorProps(actorName);
                string routeOnRole = props?.RouteOnRole;
                Props actorProps;
                if (routeOnRole == null)
                {
                    var (actions, asyncActions) = GetActions(group.AsEnumerable());
                    actorProps = Props.Create(() => new Actor<M, P>(actions, asyncActions, MessageFactory, NodeConfig, this));
                }
                else
                {
                    Logger.Debug("Deploying remote actor {0} with router {1}", actorName, props.Router);
                    actorProps =
                        new ClusterRouterPool(
                            GetRouterPool(props.Router),
                            new ClusterRouterPoolSettings(props.TotalMaxRoutees, props.MaxRouteesPerNode, props.AllowLocalRoutee, routeOnRole))
                        .Props(
                            Props.Create<Actor<M, P>>(
                                actorName, 
                                group
                                .Select(
                                    entry => 
                                    (entry.CodeType, 
                                    (entry.CodeType != EActionType.Code ? entry.Pattern.ToTuple() : (new string[] { }, false)), 
                                    entry.Code))
                                .Distinct()
                                .ToArray()));
                }
                var actor = ActorSystem.ActorOf(actorProps, actorName);
                Logger.Debug("Actor {0} created properly, path = {1}", actorName, actor.Path.ToStringWithoutAddress());
                await RegisterToGlobalDirectory(actor, group.Select(entry => entry.Pattern));
                Logger.Debug("Actor {0} registered properly", actorName);
            }
        }

        Pool GetRouterPool(string routerName)
        {
            var path = $"akka.actor.router.type-mapping.{routerName}-pool";
            var routerTypeName = ActorSystem.Settings.Config.GetString(path);
            var routerType = Type.GetType($"{routerTypeName}, Akka");
            return (Pool)Activator.CreateInstance(routerType, 0);
        }

        async Task RegisterToGlobalDirectory(IActorRef actor, IEnumerable<P> patterns)
        {
            Mediator.Tell(new Put(actor));

            var actorDirectory = new ActorDirectory<M, P>(ActorSystem, NodeConfig);
            await actorDirectory.PublishPatterns(actor.Path.ToStringWithoutAddress(), patterns);
        }

        static Action<M, ICanReceiveMessage<M>, ICanReceiveMessage<M>, MessageSystem<M, P>> PowershellScriptToAction(string script, bool respondWithOutput)
            => (message, sender, self, messageSystem) =>
            {
                var outputs = new PowershellScriptExecutor().Execute(
                    script,
                    ("message", message.ToString()),
                    ("sender", sender as ActorRefMessageReceiver<M>),
                    ("self", self as ActorRefMessageReceiver<M>),
                    ("messageSystem", messageSystem));
                if (outputs != null)
                {
                    if (respondWithOutput)
                    {
                        foreach (var output in outputs)
                            sender.Tell(messageSystem.MessageFactory.FromString(output.ToString()), self);
                    }
                    else if (MessageSystem<M, P>.Instance.Logger.IsDebugEnabled)
                    {
                        foreach (var output in outputs)
                            MessageSystem<M, P>.Instance.Logger.Debug("powershell output: {0}", output);
                    }
                }
            };

        public void Dispose()
        {
            CoordinatedShutdown.Get(ActorSystem).Run().Wait();
        }
    }

    public class XmlMessageSystem : MessageSystem<XmlMessage, XmlMessagePattern>
    {
        public XmlMessageSystem(NodeConfig nodeConfig) : 
            base(
                "XmlCluster",
                new XmlMessageFactory(),
                new XmlMessagePatternFactory(),
                nodeConfig
                )
        {
        }

        public XmlMessageSystem(ActorSystem system, NodeConfig nodeConfig) :
            base(
                system,
                new XmlMessageFactory(),
                new XmlMessagePatternFactory(),
                nodeConfig
                )
        {
        }
    }

    public class JsonMessageSystem : MessageSystem<JsonMessage, JsonMessagePattern>
    {
        public JsonMessageSystem(NodeConfig nodeConfig) : 
            base(
                "JsonCluster",
                new JsonMessageFactory(),
                new JsonMessagePatternFactory(),
                nodeConfig
                )
        {
        }

        public JsonMessageSystem(ActorSystem system, NodeConfig nodeConfig) :
            base(
                system,
                new JsonMessageFactory(),
                new JsonMessagePatternFactory(),
                nodeConfig
                )
        {
        }
    }

}
