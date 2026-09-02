# disney-solitaire-mtx-utilities

Editor tools for the Disney Solitaire MTX projects. Editor-only — nothing here ships in a build.

## Adding it to a project

```
git submodule add git@github.com:daniel-noam/disney-solitaire-mtx-utilities Assets/Shared/disney-solitaire-mtx-utilities
```

Existing clones pick it up with `git submodule update --init`.

## Assemblies

Two, split by what they need, so the toolset drops into any project:

| Assembly | Needs | Contents |
| --- | --- | --- |
| `Tools.Editor.EditorUtilities` | Unity, TextMeshPro | The portable tools |
| `Tools.Editor.EditorUtilities.Templates` | The Domino template assemblies | Anything that touches behaviour graphs or bindings |

The second is constrained to the `DOMINO_TEMPLATES` define, which template projects already
set for the shared runtime code. A project without it simply does not compile that assembly —
no missing-reference errors, and nothing to configure.

Nothing here modifies the graph editor or any other project code. The tools that appear inside
the graph editor attach themselves to it from the outside.

## The tools

Under `Utilities/` in the menu bar:

- **AssetBundle Viewer** — what is in a bundle, how big, and what it references.
- **EasyUpload** — drag a build folder, review the diff against S3, upload.
- **Folder Structure Generator** — create a folder tree from a saved profile.
- **Git Local Exclude Manager** — `.git/info/exclude` and skip-worktree, with a UI.
- **Sprite Editor** — 9-slice borders, cleanup and bleed, mask generation.
- **TMP Rich Text Builder** — compose and preview TMP rich text.
- **Material Extractor & Assigner** — pull TMP materials out and reassign them.
- **Dynamic Template Bindings Builder** — add a run of binding keys from one pattern.
- **Dynamic Template Bindings Settings** — what the bindings inspector shows.

On a `DynamicTemplateBindings` component: an issue box per finding, with buttons to add a
missing key or remove an unused one, and a right-click entry that finds the nodes using a key.

Inside the behaviour graph editor, on the toolbar:

- **Min Version** — which nodes decide the template's minimum client version.
- **Deprecated** — the deprecated nodes, and a button to swap each for its replacement,
  carrying the connections and values across.

## Working on it

It is a submodule, so changes are committed and pushed here, then picked up elsewhere with
`git submodule update --remote`. `Editor/Design/ToolStyles.cs` holds the shared design language
and the rules the windows follow; read its header before adding a tool.
