// UC-T07-08: function views own per-tile scratch, borrow accepted inputs, and retire with their parent.
using System.Collections.Generic;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Nodes
{
    public partial class TileData
    {
        private Dictionary<TextureFunctionCallNode, TileData> functionViews;
        private TextureFunctionCallNode functionCaller;
        private TileData functionParent, functionSamplingParent;

        internal TileData FunctionView(TextureFunctionCallNode call, GenerationSnapshot snapshot)
        {
            if (functionViews == null) functionViews = new Dictionary<TextureFunctionCallNode, TileData>();
            if (functionViews.TryGetValue(call, out TileData view)) return view;
            view = new TileData(chunkCoord, rect, worldPos, worldSize, worldHeight);
            CopyViewContext(view); view.functionCaller = call; view.functionParent = this;
            view.RetainGenerationInputs(snapshot); view.Bind(new GraphEvaluator(snapshot.Graph));
            functionViews.Add(call, view); return view;
        }

        internal void CopyViewContext(TileData view)
        {
            AttachMemoryView(view); view.AssignRun(RunId); view.metrics = metrics;
            view.worldGraphGround = worldGraphGround; view.drainageCache = drainageCache; view.ecology = ecology;
            view.SetFullWorld(fullWorldPos, fullWorldSize);
            view.functionCaller = functionCaller; view.functionParent = functionParent;
        }

        internal object PullFunctionResult(Generator endpoint, StopToken stop) => evaluator.PullNode(endpoint, 0, this, stop);

        internal object PullFunctionArgument(string endpointId, StopToken stop)
        {
            if (functionCaller == null || functionParent == null) return null;
            TileData parent = functionParent;
            // A function's halo must re-evaluate its arguments on that same lattice; borrowing a base-cell
            // image would duplicate/clamp the boundary instead of sampling the real neighbouring inputs.
            if (!rect.Equals(parent.rect) || !worldPos.Equals(parent.worldPos) || !worldSize.Equals(parent.worldSize))
            {
                if (functionSamplingParent == null)
                {
                    functionSamplingParent = new TileData(chunkCoord, rect, worldPos, worldSize, worldHeight);
                    parent.CopyViewContext(functionSamplingParent);
                    functionSamplingParent.Bind(new GraphEvaluator(parent.Graph));
                }
                parent = functionSamplingParent;
            }
            return functionCaller.PullArgument(endpointId, parent, stop);
        }

        private void ReleaseFunctionViews()
        {
            functionViews?.Clear(); functionCaller = null; functionParent = functionSamplingParent = null;
        }
    }
}
