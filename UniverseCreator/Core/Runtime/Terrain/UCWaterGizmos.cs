namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// One switch for the debug wire boxes the water volume and the ripple sim draw
    /// when selected. Off by default: selecting a river to edit it used to paint a
    /// blue bounds cube and a sim box over the water, which read as stray "boxes" in
    /// the scene. They are a debugging aid, not part of the water, so they are gated
    /// here and turned on only from Tools/Universe Creator/Water/Show Debug Bounds.
    ///
    /// The field lives in the runtime assembly because the gizmo callbacks that read
    /// it are runtime code; the editor menu that flips it (and remembers the choice in
    /// EditorPrefs) lives beside it in the editor assembly.
    /// </summary>
    public static class UCWaterGizmos
    {
        // Default off - the boxes only ever helped a developer, never the user placing
        // water, and they are what the "remove the box things" report is about.
        public static bool ShowDebugBounds = false;
    }
}
