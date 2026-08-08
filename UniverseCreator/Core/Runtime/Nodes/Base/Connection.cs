// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    Connection.cs
//  Purpose: Serializable edge in the node graph, linking one output port to
//           one input port. The evaluator walks backward along these to pull
//           dependencies.
// -----------------------------------------------------------------------------

using System;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// A directed edge from one Generator's output port to another's input port.
    /// Stored in the Graph's flat connection list.
    /// </summary>
    [Serializable]
    public struct Connection : IEquatable<Connection>
    {
        /// <summary>Node id (Generator.id) that produces the data.</summary>
        public string sourceNodeId;

        /// <summary>Zero-based index into that node's GetOutputs() array.</summary>
        public int sourcePortIndex;

        /// <summary>Node id that consumes the data.</summary>
        public string targetNodeId;

        /// <summary>Zero-based index into that node's GetInputs() array.</summary>
        public int targetPortIndex;

        public Connection(string sourceNodeId, int sourcePortIndex,
                          string targetNodeId, int targetPortIndex)
        {
            this.sourceNodeId = sourceNodeId;
            this.sourcePortIndex = sourcePortIndex;
            this.targetNodeId = targetNodeId;
            this.targetPortIndex = targetPortIndex;
        }

        public bool Equals(Connection other)
        {
            return sourceNodeId == other.sourceNodeId &&
                   sourcePortIndex == other.sourcePortIndex &&
                   targetNodeId == other.targetNodeId &&
                   targetPortIndex == other.targetPortIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is Connection && Equals((Connection)obj);
        }

        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 31 + (sourceNodeId != null ? sourceNodeId.GetHashCode() : 0);
            hash = hash * 31 + sourcePortIndex;
            hash = hash * 31 + (targetNodeId != null ? targetNodeId.GetHashCode() : 0);
            hash = hash * 31 + targetPortIndex;
            return hash;
        }

        public override string ToString()
        {
            return sourceNodeId + "[" + sourcePortIndex + "] -> " +
                   targetNodeId + "[" + targetPortIndex + "]";
        }
    }
}
