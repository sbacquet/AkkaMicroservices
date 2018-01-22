﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Xunit;

namespace Hexagon.AkkaImpl.UnitTests
{
    using XmlActor = Actor<XmlMessage, XmlMessagePattern>;

    public class XmlMessageTests : TestKit
    {
        bool AlwaysTrue(XmlMessage message) => true;

        [Fact]
        public void AnActorCanTellAnotherOne()
        {
            var messageFactory = new XmlMessageFactory();
            var a1 = new XmlActor.ActionWithFilter(
                (message, sender, self) => TestActor.Tell(XmlMessage.FromString("<message>done</message>")), 
                AlwaysTrue);
            var actor1 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new [] { a1 },
                messageFactory)), "actor1");
            var a2 = new XmlActor.ActionWithFilter(
                (message, sender, self) => actor1.Tell(XmlMessage.FromString("<message>OK received</message>")), 
                AlwaysTrue);
            var actor2 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new [] { a2 },
                messageFactory)), "actor2");
            actor2.Tell(XmlMessage.FromString("<message>OK?</message>"), TestActor);
            ExpectMsg<XmlMessage>(message => message.Content == "<message>done</message>");
        }

        [Fact]
        public void AnActorCanAskAnotherOne()
        {
            var messageFactory = new XmlMessageFactory();
            var a1 = new XmlActor.ActionWithFilter(
                (message, sender, self) => sender.Tell(XmlMessage.FromString("<message>OK!</message>"), self),
                AlwaysTrue);
            var actor1 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new[] { a1 },
                messageFactory)), "actor1");
            var a2 = new XmlActor.ActionWithFilter(
                (message, sender, self) =>
                {
                    var r = 
                        new ActorRefMessageReceiver<XmlMessage>(actor1)
                        .Ask(XmlMessage.FromString("<message>OK?</message>"), messageFactory)
                        .Result;
                    Assert.Equal("<message>OK!</message>", r.Content);
                    TestActor.Tell(XmlMessage.FromString("<message>done</message>"));
                },
                AlwaysTrue);
            var actor2 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new[] { a2 },
                messageFactory)), "actor2");
            actor2.Tell(XmlMessage.FromString("<message>test</message>"));
            ExpectMsg<XmlMessage>(message => message.Content == "<message>done</message>");
        }

        [Fact]
        public void AnActorCanBeAskedFromOutside()
        {
            var a1 = new XmlActor.ActionWithFilter(
                (message, sender, self) => sender.Tell(XmlMessage.FromString("<message>OK!</message>"), self),
                AlwaysTrue);
            var messageFactory = new XmlMessageFactory();
            var actor1 = Sys.ActorOf(Props.Create(() => new XmlActor(
                new[] { a1 },
                messageFactory)), "actor1");
            var r = 
                new ActorRefMessageReceiver<XmlMessage>(actor1)
                .Ask(XmlMessage.FromString("<message>test</message>"), messageFactory)
                .Result;
            Assert.True(r.Match(@"message[. = ""OK!""]"));
        }
    }
}
