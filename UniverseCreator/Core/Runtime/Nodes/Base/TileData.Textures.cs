using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    public partial class TileData
    {
        // UC-T02-06: generated graph textures are temporary evaluation resources, never imported/borrowed assets.
        // The editor applier persists independent asset copies before the tile releases these native originals.
        private sealed class OwnedTexture
        {
            internal TextureResourceOwner Resource;
        }

        // UC-T07-02: one native allocation stays charged until the tile and every explicit preview/renderer consumer retire.
        internal sealed class TextureResourceOwner
        {
            private Texture2D texture;
            private ProductMemoryBudget.Lease memory;
            private int consumers = 1;
            internal TextureResourceOwner(Texture2D texture, ProductMemoryBudget.Lease memory)
            { this.texture = texture; this.memory = memory; }
            internal IDisposable Acquire()
            {
                GraphExecutionGuard.RequireMainThread("Generated texture consumer acquisition");
                if (consumers == 0) throw new InvalidOperationException("UC-T07-02: generated texture has already retired.");
                consumers++; return new Consumer(this);
            }
            internal void Release()
            {
                GraphExecutionGuard.RequireMainThread("Generated texture consumer retirement");
                if (--consumers != 0) return;
                RetireTexture(texture, memory); texture = null; memory = null;
            }
            private sealed class Consumer : IDisposable
            {
                private TextureResourceOwner owner;
                internal Consumer(TextureResourceOwner owner) { this.owner = owner; }
                public void Dispose()
                {
                    GraphExecutionGuard.RequireMainThread("Generated texture consumer retirement");
                    if (owner == null) return;
                    TextureResourceOwner previous = owner; owner = null; previous.Release();
                }
            }
        }
        private List<OwnedTexture> ownedTextures;
        internal ProductMemoryBudget MemoryBudget => memoryBudget;
        internal ProductMemoryBudget.Lease ReserveTextureMemory(long bytes) => memoryBudget?.Reserve(0, bytes);

        internal TextureRef OwnTexture(Texture2D texture, ProductMemoryBudget.Lease memory)
        {
            GraphExecutionGuard.RequireMainThread("Generated texture ownership");
            if (ownedTextures == null) ownedTextures = new List<OwnedTexture>();
            var resource = new TextureResourceOwner(texture, memory);
            ownedTextures.Add(new OwnedTexture { Resource = resource });
            return new TextureRef(texture) { isGenerated = true, resourceOwner = resource };
        }

        private void ReleaseTextureMemory()
        {
            if (ownedTextures == null || ownedTextures.Count == 0) return;
            GraphExecutionGuard.RequireMainThread("Generated texture retirement");
            foreach (OwnedTexture owned in ownedTextures)
                owned.Resource.Release();
            ownedTextures.Clear();
        }

        internal static void RetireTexture(Texture2D texture, ProductMemoryBudget.Lease memory)
        {
            if (texture == null) { memory?.Dispose(); return; }
            GraphExecutionGuard.RequireMainThread("Generated texture retirement");
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(texture);
                ReleaseTextureNextFrame(texture, memory);
            }
            else { UnityEngine.Object.DestroyImmediate(texture); memory?.Dispose(); }
        }

        private static async void ReleaseTextureNextFrame(Texture2D texture, ProductMemoryBudget.Lease memory)
        {
            if (memory == null) return;
            try
            {
                for (int frame = 0; frame < 2; frame++)
                {
                    await Awaitable.NextFrameAsync();
                    if (texture == null) { memory.Dispose(); return; }
                }
                Debug.LogError("UC-T02-06: generated texture did not retire within two frames; its native reservation remains charged.");
            }
            catch (Exception error) { Debug.LogError("UC-T02-06: generated texture retirement failed: " + error); }
        }
    }
}
