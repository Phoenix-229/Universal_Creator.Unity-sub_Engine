using System;
using System.Collections.Generic;

namespace UniverseCreator.Nodes
{
    public partial class TileData
    {
        // UC-T02-07: each ancestor captures all publications of the inputs it used, including retained ancestors that skip pulls.
        private sealed class PublicationFrame
        {
            internal string Key;
            internal readonly Dictionary<string, GraphPublication> Items = new Dictionary<string, GraphPublication>(StringComparer.Ordinal);
            internal PublicationFrame(string key) { Key = key; }
        }
        private readonly Stack<PublicationFrame> publicationFrames = new Stack<PublicationFrame>();
        private readonly Stack<PublicationFrame> sparePublicationFrames = new Stack<PublicationFrame>();
        private readonly Dictionary<string, GraphPublication[]> nodePublications = new Dictionary<string, GraphPublication[]>();
        private readonly Dictionary<string, GraphPublication> pendingPublications = new Dictionary<string, GraphPublication>(StringComparer.Ordinal);
        private readonly HashSet<string> replayedPublications = new HashSet<string>(StringComparer.Ordinal);
        private TileData publicationParent;

        internal string CurrentPublicationKey => publicationFrames.Count > 0 ? publicationFrames.Peek().Key : "direct";
        internal void BeginPublications(string key)
        {
            PublicationFrame frame = sparePublicationFrames.Count > 0 ? sparePublicationFrames.Pop() : new PublicationFrame(key);
            frame.Key = key;
            publicationFrames.Push(frame);
        }
        internal GraphPublication[] CurrentPublications() => publicationFrames.Count == 0
            ? Array.Empty<GraphPublication>() : PublicationArray(publicationFrames.Peek().Items);

        internal void EndPublications(bool succeeded)
        {
            PublicationFrame frame = publicationFrames.Pop();
            if (succeeded)
            {
                GraphPublication[] items = PublicationArray(frame.Items);
                nodePublications[frame.Key] = items;
                foreach (GraphPublication item in items) AddPublication(item, true);
            }
            frame.Items.Clear();
            sparePublicationFrames.Push(frame);
        }

        internal void AddPublication(GraphPublication publication, bool alreadyApplied = false)
        {
            if (replayedPublications.Add(publication.Key) && !alreadyApplied) publication.ReplayEvaluation(this);
            if (publicationFrames.Count > 0) publicationFrames.Peek().Items[publication.Key] = publication;
            else
            {
                pendingPublications[publication.Key] = publication;
                // A coarse/halo view contributes deferred scene metadata, never a second canopy stamp on the parent grid.
                if (publicationParent != null && publication is ChannelGraphPublication) publicationParent.AddPublication(publication, true);
            }
        }

        internal void ReplayPublications(string nodeKey)
        {
            if (nodePublications.TryGetValue(nodeKey, out GraphPublication[] publications)) ReplayPublications(publications);
        }
        internal void ReplayPublications(GraphPublication[] publications)
        {
            if (publications != null) foreach (GraphPublication publication in publications) AddPublication(publication);
        }

        internal ChannelGraphPublication[] ChannelPublications()
        {
            var result = new List<ChannelGraphPublication>();
            foreach (GraphPublication publication in PublicationArray(pendingPublications))
                if (publication is ChannelGraphPublication channel) result.Add(channel);
            return result.ToArray();
        }

        internal void FinishPublications()
        {
            outputs.channelPublications = ChannelPublications();
            outputs.publicationBounds = new UnityEngine.Rect(worldPos.x, worldPos.z, worldSize.x, worldSize.z);
            outputs.publicationOwner = "chunk:" + chunkCoord.x + ":" + chunkCoord.z;
            outputs.publicationOriginY = worldPos.y; outputs.publicationHeight = worldHeight; // UC-T03-09: never read mutable world units at commit.
        }

        private static GraphPublication[] PublicationArray(Dictionary<string, GraphPublication> publications)
        {
            if (publications.Count == 0) return Array.Empty<GraphPublication>();
            var result = new GraphPublication[publications.Count]; publications.Values.CopyTo(result, 0);
            Array.Sort(result, (left, right) => string.CompareOrdinal(left.Key, right.Key));
            return result;
        }

        private void ReleasePublications()
        {
            nodePublications.Clear(); pendingPublications.Clear(); replayedPublications.Clear(); publicationFrames.Clear();
            sparePublicationFrames.Clear();
            publicationParent = null;
        }
    }
}
