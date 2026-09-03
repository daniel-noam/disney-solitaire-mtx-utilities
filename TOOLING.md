# Tooling guidelines

How a tool gets added here, and the rules that keep the toolset droppable into any project.
For how it should *look*, see [DESIGN.md](DESIGN.md).

## Which assembly

| Your tool needs | It goes in | Folder |
| --- | --- | --- |
| Only Unity and TextMeshPro | `Utilities.Editor` | `Editor/YourTool/` |
| Behaviour graphs, bindings, or anything Domino | `Utilities.Editor.Templates` | `Editor/Templates/YourTool/` |

The second assembly is constrained to the `DOMINO_TEMPLATES` define, so in a project without the
template system Unity leaves it out of the compilation entirely. That is what lets this repo be
added anywhere.

**Put a tool in the core only if it truly has no project dependency.** One `using` of a Domino type
in the core breaks the toolset for every project that does not have it — which is the failure this
split exists to prevent, and it will not show up on your machine.

## Never require a change to project code

A tool here must work by attaching to the project from the outside. If it only works once somebody
edits a file in the game repo, it is not portable, and the next person to clone gets a tool that
silently does nothing.

This is not theoretical: the graph editor's toolbar has no extension point, and the first version of
the Min Version panel added a registry to `GraphEditorWindow` to get one. That made a tool in this
repo depend on an edit in another, and it was replaced by
`Editor/Templates/GraphPanels/GraphToolPanelInjector.cs`, which finds open graph windows and hangs
panels on them with nothing but public API.

The trade is real and worth stating: attaching from outside binds you to *layout* rather than to a
contract — the injector finds the toolbar by type and the search box by its USS class — so it fails
by quietly not appearing rather than by failing to compile. Prefer a fallback where one exists, and
say so in a comment where one does not.

Where the framework does offer a seam, use it: `[CustomNodeView]` and `ISearchProvider` are both
found by assembly scan and need no edits at all.

## Menu items

Under `Utilities/`, titled to match the menu item exactly, and numbered in the band for its
assembly — 1000 for the portable tools, 2000 for the template ones. See
[DESIGN.md](DESIGN.md#menu-items) for why the bands are what draw the separator.

## Editing project assets

Anything that writes to a graph, a prefab or a component:

- Go through `SerializedProperty` where you can, so undo and prefab overrides behave like a hand
  edit.
- Take the undo snapshot **once per thing the person did**, in the caller — not inside the routine
  that does the work. `Undo.RecordObject` snapshots the whole asset, and a batch of forty taking
  forty snapshots is both slow and wrong: it is one action to undo.
- Say what a destructive action will cost *before* it is taken, and what it actually cost after. A
  count in a button label beats a confirmation dialog.
- Never silently drop something. If a swap cannot carry a connection across, that belongs in a list
  the person can work from afterwards, and in the console.

## Verifying

Unity is slow to round-trip, so compile directly against its own response file:

```
R=$(ls -t Library/Bee/artifacts/*/Utilities.Editor.rsp | head -1)
sed -e 's#^-out:.*#-out:"/tmp/out.dll"#' -e '/^-refout:/d' $R > /tmp/my.rsp
echo "-warnaserror+" >> /tmp/my.rsp
$UNITY/MonoBleedingEdge/bin/mono \
  $UNITY/MonoBleedingEdge/lib/mono/msbuild/Current/bin/Roslyn/csc.exe @/tmp/my.rsp
```

Run it from the project root — the paths inside are relative. It carries every define and reference
Unity uses, which a hand-built response file does not.

Two traps: the file's last line has no trailing newline, so appending a source without printing one
first silently concatenates it onto the previous argument; and the response file is a snapshot, so a
file added since Unity last compiled has to be appended and a deleted one filtered out.

`-warnaserror+` is worth keeping on. Most of what it catches here is real.

## Settings

Per-user preferences go in `EditorPrefs` as JSON, following `CleanupOptions` or
`DynamicTemplateBindingsSettings`. They are working preferences, not project data, so they must not
land in `ProjectSettings/` or in an asset — those are tracked, and a tool should not put a diff in
somebody's branch for remembering a checkbox.

The one exception is a scripting define, which Unity only keeps in `ProjectSettings.asset`. Do not
write one from a script; key off a define the project already sets, as the Templates assembly does
with `DOMINO_TEMPLATES`.

## Comments

Say why, not what. The code already says what it does; what it cannot say is which of two
reasonable-looking options was wrong, and why the obvious one is not what is written. A comment
that survives is one that stops somebody undoing a fix.
