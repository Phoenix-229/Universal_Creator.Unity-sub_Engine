// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    AssemblyInfo.cs
//  Purpose: Assembly-level attributes for the UniverseCreator runtime assembly.
//
//  The editor/diagnostics assembly (UniverseCreator.Editor) is part of the SAME
//  shipped toolkit, not a buyer's assembly, so it is allowed to reach a small
//  number of `internal` test-construction seams (e.g. WorldDrainage.CreateForTests)
//  that must not be part of the public API. This does NOT widen the surface a
//  buyer's own code can see - `internal` stays hidden from every assembly except
//  the named friend.
// -----------------------------------------------------------------------------

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UniverseCreator.Editor")]
