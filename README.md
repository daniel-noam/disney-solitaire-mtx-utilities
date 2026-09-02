# disney-solitaire-mtx-utilities

Editor tools for the Disney Solitaire MTX projects. Editor-only — nothing here ships in a build.

## Adding it to a project

### In Fork

1. Open the project's repository in Fork, and make sure you are on the branch you want the
   submodule added to — it lands as a commit on that branch.
2. **Repository → Add Submodule…**
3. Fill in the two fields:
   - **URL**: `git@github.com:daniel-noam/disney-solitaire-mtx-utilities`
   - **Path**: `Assets/Shared/disney-solitaire-mtx-utilities`

   The path matters. Unity finds assets by where they sit, and the assembly definitions expect
   this one, so putting it elsewhere means the tools compile but nothing else in the project can
   see them.
4. **Add Submodule**, then commit and push. The commit contains `.gitmodules` and a pointer to
   the exact commit of this repo — not its files.

<!-- Add screenshot of Fork's Add Submodule dialog here -->

### On the command line

```
git submodule add git@github.com:daniel-noam/disney-solitaire-mtx-utilities Assets/Shared/disney-solitaire-mtx-utilities
```

### Once somebody else has added it

Cloning a repository does not bring its submodules down with it — a fresh clone leaves this
folder empty, and Unity will report the missing assemblies rather than the missing folder.

- **Fork**: it appears under Submodules in the sidebar; right-click it and choose **Init**, or
  use **Repository → Update Submodules**.
- **Command line**: `git submodule update --init`

### Getting later versions

A submodule is pinned to one commit, so new work here does not arrive on its own — that is the
point, since it means this repo cannot change under a branch that was working.

- **Fork**: right-click the submodule → **Fetch**, then **Update**.
- **Command line**: `git submodule update --remote`

Either way the project repo now points at a newer commit, which is itself a change to commit.

## Assemblies

Two, split by what they need, so the toolset drops into any project:

| Assembly | Needs | Contents |
| --- | --- | --- |
| `Utilities.Editor` | Unity, TextMeshPro | The portable tools |
| `Utilities.Editor.Templates` | The Domino template assemblies | Anything that touches behaviour graphs or bindings |

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
`git submodule update --remote`.

Before adding a tool:

- **[TOOLING.md](TOOLING.md)** — which assembly it belongs in, why a tool must never require a
  change to project code, editing assets safely, and how to compile-check without waiting for Unity.
- **[DESIGN.md](DESIGN.md)** — the shared look, the rules behind it, and the three habits that no
  style can enforce for you.
