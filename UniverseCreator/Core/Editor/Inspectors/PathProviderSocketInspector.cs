// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    PathProviderSocketInspector.cs
//  Purpose: The socket's authoring surface: enter an external path tool's link,
//           read back whether it was accepted, what it calls itself, which
//           provider surface it speaks, and how much provider-owned ground it is
//           holding right now.
//
//  WHY THE STATUS IS SHOWN ON THIS SIDE TOO
//  ----------------------------------------
//  A two-product integration that only reports on one side is debuggable by the
//  person who wrote it and by nobody else. The author who entered the link is
//  standing in this inspector, so this is where the answer belongs: accepted or
//  refused, the name the tool gives itself, and the held-region count that shows
//  whether a release actually happened.
//
//  NOTHING HERE RE-DECIDES ANYTHING
//  --------------------------------
//  Every line is read from PathProviderSocket.Status(). A second opinion computed
//  in the inspector is how a panel and a component end up disagreeing in front of
//  the user.
// -----------------------------------------------------------------------------

using System.Globalization;
using UnityEditor;
using UnityEngine;
using UniverseCreator.Integration;

namespace UniverseCreator.Editor.Inspectors
{
    [CustomEditor(typeof(PathProviderSocket))]
    public class PathProviderSocketInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            PathProviderSocket socket = (PathProviderSocket)target;
            PathLinkStatus status = socket.Status();

            UCInspectorUI.HeaderBar("Path Provider Socket", "d_Linked", AccentFor(status.state),
                HeadlineFor(status.state));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enteredPathTool"),
                new GUIContent("Path tool link",
                    "The external path tool's link component. Nothing is searched for: an entered reference is the "
                    + "author's statement of which tool may request terrain changes."));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(status.sentence, MessageTypeFor(status.state));

            if (status.SocketFilled)
            {
                EditorGUILayout.LabelField("Reported name", string.IsNullOrEmpty(status.toolName)
                    ? "(none given)" : status.toolName);
            }

            if (status.state == PathLinkState.Accepted || status.state == PathLinkState.SurfaceMismatch)
            {
                EditorGUILayout.LabelField("Provider surface",
                    "publishes " + status.publishedMembers.ToString(CultureInfo.InvariantCulture)
                    + " members, the tool understands "
                    + status.understoodMembers.ToString(CultureInfo.InvariantCulture));
                EditorGUILayout.LabelField("Tool reports", status.toolReportsConnected
                    ? "connected to this provider" : "entered here, not currently routing work");
                EditorGUILayout.LabelField("Holding now",
                    status.heldRegions.ToString(CultureInfo.InvariantCulture)
                    + (status.heldRegions == 1 ? " region (pad, cut or opening)"
                                               : " regions (pads, cuts, openings)"));
            }

            EditorGUILayout.Space(6f);
            using (new EditorGUI.DisabledScope(!status.SocketFilled))
            {
                if (UCInspectorUI.TintedButton(new GUIContent("Clear the socket",
                        "Asks the tool to give back every region it holds, then empties the field. The release happens "
                        + "first on purpose: a reference dropped first leaves holds nobody can find."),
                        UCInspectorUI.ButtonAmber))
                {
                    Undo.RecordObject(socket, "Clear path provider socket");
                    int released = socket.Clear();
                    EditorUtility.SetDirty(socket);
                    Debug.Log("Path provider socket cleared; the tool released "
                        + released.ToString(CultureInfo.InvariantCulture) + " held region(s).", socket);
                }
            }
        }

        private static string HeadlineFor(PathLinkState state)
        {
            if (state == PathLinkState.Accepted) { return "connected"; }
            if (state == PathLinkState.Empty) { return "no tool connected"; }
            if (state == PathLinkState.Vanished) { return "link missing"; }
            if (state == PathLinkState.SurfaceMismatch) { return "version drift"; }
            return "not a link";
        }

        private static Color AccentFor(PathLinkState state)
        {
            if (state == PathLinkState.Accepted) { return UCInspectorUI.HeaderGreen; }
            if (state == PathLinkState.Empty) { return UCInspectorUI.HeaderGrey; }
            return UCInspectorUI.HeaderAmber;
        }

        private static MessageType MessageTypeFor(PathLinkState state)
        {
            if (state == PathLinkState.Accepted) { return MessageType.Info; }
            if (state == PathLinkState.Empty) { return MessageType.None; }
            return MessageType.Warning;
        }
    }
}
