using System;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>Which tool the window is showing. The value is the tab order.</summary>
    public enum SpriteEditorToolId
    {
        NineSlice = 0,
        Mask = 1,
        Cleanup = 2,
    }

    /// <summary>
    /// One tab of <see cref="SpriteEditorWindow"/>. The window owns the texture, the snapshot of its
    /// pixels and the zoom/pan state; a tool only draws into the rects it is handed and writes its
    /// own output.
    ///
    /// Subclasses are plain [Serializable] classes held in fields on the window, so their settings
    /// survive a domain reload the same way the window's own do.
    /// </summary>
    [Serializable]
    public abstract class SpriteEditorTool
    {
        [NonSerialized] private SpriteEditorWindow window;

        public abstract string DisplayName { get; }

        /// <summary>
        /// What the current settings will actually do, spelled out.
        ///
        /// Drawn by the window directly above the buttons that commit it rather than at the end of
        /// the settings — it describes what pressing them does, so it has to be visible when you
        /// press them, not scrolled off with the options it summarises.
        /// </summary>
        public virtual string Summary => string.Empty;

        protected SpriteEditorWindow Window => window;
        protected SpriteTarget Target => window == null ? null : window.Target;
        protected SpriteSnapshot Snapshot => window == null ? null : window.Snapshot;

        /// <summary>Re-attached on every enable, because the reference itself is not serialized.</summary>
        public void Attach(SpriteEditorWindow owner)
        {
            window = owner;
        }

        /// <summary>A different texture was loaded, or the same one was re-read from disk.</summary>
        public virtual void OnTargetChanged()
        {
        }

        /// <summary>Window closing or reloading: drop generated textures and flush preferences.</summary>
        public virtual void OnDisable()
        {
        }

        /// <summary>Window lost focus. The moment to write EditorPrefs, not every keystroke.</summary>
        public virtual void FlushPreferences()
        {
        }

        /// <summary>Extra controls on the preview toolbar, right of the zoom controls.</summary>
        public virtual void DrawToolbar()
        {
        }

        /// <summary>
        /// A key press, before the window looks at it. Return true to claim it. Only called with a
        /// texture loaded and no text field being edited.
        /// </summary>
        public virtual bool HandleShortcut(Event e)
        {
            return false;
        }

        /// <summary>
        /// One warning above the preview, or null for none. Must depend only on state that changes
        /// outside a GUI pass (the target, the snapshot, saved options) - a warning that appears
        /// half way through a frame changes the control count and breaks IMGUI layout groups.
        /// </summary>
        public virtual string GetWarning()
        {
            return null;
        }

        /// <summary>
        /// Draws inside the preview surface. <paramref name="view"/> is already clipped and rebased
        /// on the surface, so it always starts at (0,0).
        /// </summary>
        public abstract void DrawPreview(Rect view);

        /// <summary>Controls between the preview and the bottom of the window.</summary>
        public virtual void DrawBelowPreview()
        {
        }

        /// <summary>Settings, in the right-hand column's scroll view.</summary>
        public abstract void DrawOptions();

        /// <summary>Buttons pinned to the bottom of the right-hand column.</summary>
        public abstract void DrawActions();
    }
}
