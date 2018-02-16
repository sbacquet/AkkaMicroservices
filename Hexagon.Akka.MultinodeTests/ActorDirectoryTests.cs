﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.TestKit;
using Akka.Remote.TestKit;
using Akka.TestKit;
using Akka.Configuration;
using Akka.DistributedData;
using FluentAssertions;
using Xunit;

namespace Hexagon.AkkaImpl.MultinodeTests
{
    public class ActorDirectoryTestsConfig : MultiNodeConfig
    {
        public RoleName First { get; }
        public RoleName Second { get; }
        public RoleName Third { get; }

        public ActorDirectoryTestsConfig()
        {
            First = Role("first");
            Second = Role("second");
            Third = Role("third");

            CommonConfig = ConfigurationFactory.ParseString(@"
                akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider, Akka.Cluster""
                akka.loglevel = DEBUG
                akka.log-dead-letters-during-shutdown = on
                akka.test.timefactor = 10
            ").WithFallback(DistributedData.DefaultConfig());

            TestTransport = true;
        }
    }

    public class ActorDirectoryTests : MultiNodeClusterSpec
    {
        #region setup 
        private readonly RoleName _first;
        private readonly RoleName _second;
        private readonly RoleName _third;
        IActorRef _replicator;
        ActorDirectory<XmlMessage, XmlMessagePattern> _actorDirectory;
        NodeConfig _nodeConfig;

        public ActorDirectoryTests() : this(new ActorDirectoryTestsConfig())
        {
        }

        protected ActorDirectoryTests(ActorDirectoryTestsConfig config) : base(config, typeof(ActorDirectoryTests))
        {
            _first = config.First;
            _second = config.Second;
            _third = config.Third;
        }

        private void Join(RoleName from, RoleName to)
        {
            RunOn(() =>
            {
                Cluster.Join(Node(to).Address);
                _replicator = DistributedData.Get(Sys).Replicator;
                _nodeConfig = new NodeConfig(from.Name);
                _actorDirectory = new ActorDirectory<XmlMessage, XmlMessagePattern>(Sys, _nodeConfig);
            }, from);
            EnterBarrier(from.Name + "-joined");
        }
        #endregion

        [MultiNodeFact]
        public void Tests()
        {
            Must_startup_3_nodes_cluster();
            ActorDirectoryMustGetInSync();
        }

        void Must_startup_3_nodes_cluster()
        {
            Within(TimeSpan.FromSeconds(15), () =>
            {
                Join(_first, _first);
                Join(_second, _first);
                Join(_third, _first);
                EnterBarrier("after-1");
            });
        }

        void ActorDirectoryMustGetInSync()
        {
            Within(TimeSpan.FromSeconds(30), () =>
            {
                RunOn(() =>
                {
                    _actorDirectory
                    .PublishPatterns(
                        "/user/test1",
                        new[]
                        {
                            new XmlMessagePattern(
                                new []
                                {
                                    @"/root/value1[. = 1]",
                                    @"/root/value2[@attr = ""a""]"
                                }),
                            new XmlMessagePattern(
                                new []
                                {
                                    @"/root/value2[. = 2]"
                                })
                        })
                    .Wait();
                }, _first);

                RunOn(() =>
                {
                    _actorDirectory
                    .PublishPatterns(
                        "/user/test2",
                        new[]
                        {
                            new XmlMessagePattern(
                                new []
                                {
                                    @"/root/value3[. = 3]"
                                })
                        })
                    .Wait();
                }, _second);

                EnterBarrier("2-registered");
                //System.Threading.Thread.Sleep(TimeSpan.FromSeconds(_nodeConfig.GossipTimeFrameInSeconds));
                if (this.Myself == _third) System.Diagnostics.Debugger.Launch();
                var watcher = Sys.ActorOf(Props.Create(() => new PatternUnpublisherActor<XmlMessage, XmlMessagePattern>(_actorDirectory)), "watcher");
                bool ready = false;
                do
                {
                    ready = watcher.Ask<bool>(PatternUnpublisherActor<XmlMessage, XmlMessagePattern>.IsReady.Instance).Result;
                } while (!ready);

                RunOn(() =>
                {
                    var patternFactory = new XmlMessagePatternFactory();
                    string xml = @"<root><value1>1</value1><value2 attr=""b"">2</value2><value3>3</value3></root>";
                    var actorPaths = _actorDirectory.GetMatchingActors(XmlMessage.FromString(xml), patternFactory).Result.Select(ma => ma.Path);
                    actorPaths.Should().BeEquivalentTo("/user/test1", "/user/test2");
                }, _first, _second, _third);

                EnterBarrier("3-done");
            });
        }
    }
}
