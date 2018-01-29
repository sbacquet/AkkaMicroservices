﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.DistributedData;
using Akka.Cluster;
using Akka.Event;

namespace Hexagon.AkkaImpl
{
    public class ActorDirectory<M, P> 
        where P : IMessagePattern<M>
    {
        readonly ActorSystem System;
        readonly ILoggingAdapter Logger;
        readonly NodeConfig NodeConfig;

        public ActorDirectory(ActorSystem actorSystem, NodeConfig nodeConfig)
        {
            System = actorSystem;
            Logger = Logging.GetLogger(System, this);
            NodeConfig = nodeConfig;
        }

        public struct ActorProps
        {
            public P[] Patterns;
            public int MistrustFactor;
        }
        public struct MatchingActor
        {
            public string Path;
            public int MatchingScore;
            public int MistrustFactor;
            public bool IsSecondary;
        }
        public async Task<IEnumerable<MatchingActor>> GetMatchingActors(M message, IMessagePatternFactory<P> messagePatternFactory)
        {
            var replicator = DistributedData.Get(System).Replicator;
            var keysResponse = await replicator.Ask<GetKeysIdsResult>(Dsl.GetKeyIds);
            var actorPaths = keysResponse.Keys;
            var matchingActors = new List<MatchingActor>();
            var readConsistency = ReadLocal.Instance; //new ReadAll(TimeSpan.FromSeconds(5));
            foreach (string path in actorPaths)
            {
                //var setKey = new GSetKey<(GSet<string> Conjuncts, bool IsSecondary)>(path);
                var setKey = new LWWRegisterKey<ActorProps>(path);
                var getResponse = await replicator.Ask<IGetResponse>(Dsl.Get(setKey, readConsistency));
                if (getResponse.IsSuccessful)
                {
                    var actorProps = getResponse.Get(setKey).Value;
                    var matchingPatterns = actorProps.Patterns.Where(pattern => pattern.Match(message));
                    int matchingPatternsCount = matchingPatterns.Count();
                    if (matchingPatternsCount > 0)
                    {
                        if (matchingPatternsCount > 1)
                        {
                            Logger.Warning("For actor {0}, found {1} handlers matching message {2}", path, matchingPatternsCount, message);
                        }
                        var matchingPattern = matchingPatterns.First();
                        matchingActors.Add(
                            new MatchingActor
                            {
                                Path = path,
                                IsSecondary = matchingPattern.IsSecondary,
                                MatchingScore = matchingPattern.Conjuncts.Length,
                                MistrustFactor = actorProps.MistrustFactor
                            });
                    }
                }
            }
            return matchingActors;
        }

        public async Task PublishPatterns(string actorPath, IEnumerable<P> patterns)
        {
            if (!patterns.Any())
                throw new Exception("cannot distribute empty pattern list");

            var cluster = Cluster.Get(System);
            int mistrustFactor = NodeConfig.GetMistrustFactor(actorPath);
            var register = 
                new LWWRegister<ActorProps>(
                    cluster.SelfUniqueAddress, 
                    new ActorProps()
                    {
                        Patterns = patterns.ToArray(),
                        MistrustFactor = mistrustFactor
                    }
                );

            var replicator = DistributedData.Get(System).Replicator;
            var setKey = new LWWRegisterKey<ActorProps>(actorPath);
            var writeConsistency = WriteLocal.Instance; //new WriteAll(TimeSpan.FromSeconds(5));
            var updateResponse = await replicator.Ask<IUpdateResponse>(Dsl.Update(setKey, register, writeConsistency));
            if (!updateResponse.IsSuccessful)
            {
                throw new Exception($"cannot update patterns for actor path {actorPath}");
            }
        }
    }
}
