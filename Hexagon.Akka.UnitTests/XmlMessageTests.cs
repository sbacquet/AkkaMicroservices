﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Xunit;
using FluentAssertions;

namespace Hexagon.AkkaImpl.UnitTests
{
    using XmlActor = Actor<XmlMessage, XmlMessagePattern>;

    public class XmlMessageTests : TestKit
    {
        bool AlwaysTrue(XmlMessage message) => true;

        NodeConfig _nodeConfig = new NodeConfig("XmlMessageTests");

        [Fact]
        public void AnActorCanTellAnotherOne()
        {
            var messageFactory = new XmlMessageFactory();
            var a1 = new XmlActor.ActionWithFilter
            {
                Action = (message, sender, self, resource, ms, logger) => TestActor.Tell(XmlMessage.FromString("<message>done</message>")),
                Filter = AlwaysTrue
            };
            var actor1 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new [] { a1 },
                null,
                messageFactory,
                null,
                null)), "actor1");
            var a2 = new XmlActor.ActionWithFilter
            {
                Action = (message, sender, self, resource, ms, logger) => actor1.Tell(XmlMessage.FromString("<message>OK received</message>")),
                Filter = AlwaysTrue
            };
            var actor2 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new [] { a2 },
                null,
                messageFactory,
                null,
                null)), "actor2");
            actor2.Tell(XmlMessage.FromString("<message>OK?</message>"), TestActor);
            ExpectMsg<XmlMessage>(message => message.Content == "<message>done</message>");
        }

        [Fact]
        public void AnActorCanAskAnotherOne()
        {
            var messageFactory = new XmlMessageFactory();
            var a1 = new XmlActor.ActionWithFilter
            {
                Action = (message, sender, self, resource, ms, logger) => sender.Tell(XmlMessage.FromString("<message>OK!</message>"), self),
                Filter = AlwaysTrue
            };
            var actor1 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new[] { a1 },
                null,
                messageFactory,
                null,
                null)), "actor1");
            var a2 = new XmlActor.AsyncActionWithFilter
            {
                Action = async (message, sender, self, resource, ms, logger) =>
                {
                    var r = await
                        new ActorRefMessageReceiver<XmlMessage>(actor1)
                        .AskAsync(XmlMessage.FromString("<message>OK?</message>"), messageFactory);
                    Assert.Equal("<message>OK!</message>", r.Content);
                    TestActor.Tell(XmlMessage.FromString("<message>done</message>"));
                },
                Filter = AlwaysTrue
            };
            var actor2 = Sys.ActorOf(Props.Create(() => new XmlActor(
                null,
                new[] { a2 },
                messageFactory,
                null,
                null)), "actor2");
            actor2.Tell(XmlMessage.FromString("<message>test</message>"));
            ExpectMsg<XmlMessage>(message => message.Content == "<message>done</message>");
        }

        [Fact]
        public void AnActorCanBeAskedFromOutside()
        {
            var a1 = new XmlActor.ActionWithFilter
            {
                Action = (message, sender, self, resource, ms, logger) => sender.Tell(XmlMessage.FromString("<message>OK!</message>"), self),
                Filter = AlwaysTrue
            };
            var messageFactory = new XmlMessageFactory();
            var actor1 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new[] { a1 },
                null,
                messageFactory,
                null,
                null)), "actor1");
            var r = 
                new ActorRefMessageReceiver<XmlMessage>(actor1)
                .AskAsync(XmlMessage.FromString("<message>test</message>"), messageFactory)
                .Result;
            r.Match(@"message[. = ""OK!""]").Should().BeTrue();
        }

        [PatternActionsRegistration]
        static void Register(PatternActionsRegistry<XmlMessage, XmlMessagePattern> registry)
        {
            registry.AddAction(new XmlMessagePattern("*"), (m, sender, self, resource, ms, logger) => { }, "actor");
        }

        [Fact]
        public void ActorFromAssembly()
        {
            var registry = new PatternActionsRegistry<XmlMessage, XmlMessagePattern>();
            registry.AddFromAssembly(System.Reflection.Assembly.GetExecutingAssembly().GetName().FullName, null);
            var lookup = registry.LookupByProcessingUnit();
            lookup.Contains("actor").Should().BeTrue();
            lookup["actor"].Count().Should().Be(1);
            lookup["actor"].First().Pattern.Conjuncts[0].Should().Be("*");
        }
    }
}
