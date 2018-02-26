﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Management.Automation;
using Hexagon;
using Hexagon.AkkaImpl;

namespace Hexagon.Automation.Cmdlets
{
    [Cmdlet(VerbsLifecycle.Start, "XmlMessageSystemClient")]
    public class StartXmlMessageSystemClient : PSCmdlet
    {
        public enum EImplType { Akka }

        [Parameter(Mandatory = false, DontShow = true)]
        public EImplType ImplType { get; set; } = EImplType.Akka;

        [Parameter(Mandatory = true, Position = 0)]
        public string NodeConfig { get; set; }

        protected override void EndProcessing()
        {
            var config = Hexagon.NodeConfig.FromFile<AkkaNodeConfig>(NodeConfig);
            var cleanConfig = new AkkaNodeConfig(config.NodeId) { SystemName = config.SystemName };
            foreach (var seedNode in config.SeedNodes)
                cleanConfig.AddSeedNode(seedNode);
            MessageSystem <XmlMessage, XmlMessagePattern> xmlMessageSystem = null;
            switch (ImplType)
            {
                case EImplType.Akka:
                    xmlMessageSystem = AkkaXmlMessageSystem.Create(cleanConfig);
                    break;
                default:
                    throw new ArgumentException("only Akka implementation type is handled");
            }
            xmlMessageSystem.Start(config);
            WriteObject(xmlMessageSystem);
        }
    }
}
