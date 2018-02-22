﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace Hexagon
{
    [XmlRoot("Node")]
    public class NodeConfig
    {
        public class ProcessingUnitProps
        {
            public ProcessingUnitProps() { }
            public ProcessingUnitProps(string name) { Name = name; }
            [XmlAttribute("Name")]
            public string Name;
            public bool Untrustworthy = false;
            public int MistrustFactor = 1;
            public string RouteOnRole = null;
            public string Router = null;
            public int TotalMaxRoutees = 1;
            public int MaxRouteesPerNode = 1;
            public bool AllowLocalRoutee = false;
        }
        [XmlIgnore]
        Dictionary<string, ProcessingUnitProps> ProcessingUnitPropsDict;

        public string NodeId;
        public string SystemName;
        [XmlArrayItem("Address")]
        public List<string> SeedNodes { get; private set; }
        [XmlArrayItem("Name")]
        public List<string> Roles { get; private set; }
        [XmlArrayItem("Name")]
        public List<string> Assemblies { get; private set; }
        [XmlArrayItem("ProcessingUnit")]
        public ProcessingUnitProps[] ProcessingUnits
        {
            get => ProcessingUnitPropsDict.Values.ToArray();
            set => ProcessingUnitPropsDict = value.ToDictionary(actorProps => actorProps.Name);
        }
        public double GossipTimeFrameInSeconds;
        public int GossipSynchroAttemptCount;

        public NodeConfig()
        {
            SystemName = "MessageSystem";
            NodeId = "node1";
            GossipTimeFrameInSeconds = 5;
            GossipSynchroAttemptCount = 3;
            Roles = new List<string>();
            Assemblies = new List<string>();
            ProcessingUnitPropsDict = new Dictionary<string, ProcessingUnitProps>();
            SeedNodes = new List<string>();
        }

        public NodeConfig(string nodeId) : this()
        {
            NodeId = nodeId;
        }

        public static T FromFile<T>(string filePath)
            where T : NodeConfig
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(T));
            using (var reader = new System.IO.StreamReader(filePath))
            {
                return (T)ser.Deserialize(reader);
            }
        }

        public void ToFile<T>(string filePath)
            where T : NodeConfig
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(T));
            using (var writer = new System.IO.StreamWriter(filePath))
            {
                ser.Serialize(writer, this);
            }
        }

        public string GetProcessingUnitName(string processingUnitId)
            => $"{NodeId}_{processingUnitId}";

        public ProcessingUnitProps GetProcessingUnitProps(string processingUnitId)
            => ProcessingUnitPropsDict.TryGetValue(processingUnitId, out ProcessingUnitProps props) ? props : null;

        public int GetMistrustFactor(string processingUnitId)
            => GetProcessingUnitProps(processingUnitId)?.MistrustFactor ?? 1;

        public void SetProcessingUnitProps(ProcessingUnitProps props)
        {
            if (props.Untrustworthy)
                props.MistrustFactor = props.MistrustFactor > 1 ? props.MistrustFactor : 2;
            else
                props.MistrustFactor = 1;
            ProcessingUnitPropsDict[props.Name] = props;
        }

        public void AddRole(string role)
            => Roles.Add(role);

        public void AddAssembly(string assembly)
            => Assemblies.Add(assembly);

        public void AddThisAssembly()
            => AddAssembly(System.Reflection.Assembly.GetCallingAssembly().GetName().Name);

        public void AddSeedNode(string node)
            => SeedNodes.Add(node);
    }
}
