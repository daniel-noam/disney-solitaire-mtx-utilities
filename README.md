# disney-solitaire-mtx-utilities

Editor tools for the Disney Solitaire MTX projects. Editor-only — nothing here ships in a build.

## Adding it to a project

These tools are **cloned into** a project, not added as a submodule. The project's git ends up
with no record of them at all — nothing to commit, nothing to stage or discard, and nothing for
somebody who does not want them to inherit.

### In Fork

1. **File → Clone**
2. Fill in:
   - **Repository URL**: `git@github.com:daniel-noam/disney-solitaire-mtx-utilities`
   - **Parent directory**: your project's `Assets/Shared`
   - **Name**: `disney-solitaire-mtx-utilities`
3. Clone. Unity will import it, and the tools appear under `Utilities/` in the menu bar.

<!-- Add screenshot of Fork's Clone dialog here -->

### On the command line

```
git clone git@github.com:daniel-noam/disney-solitaire-mtx-utilities \
    Assets/Shared/disney-solitaire-mtx-utilities
```

### It hides itself

You do not have to tell the project to ignore it. On first load the tools write two local,
untracked things:

- `Assets/Shared/.gitignore`, listing this folder and its `.meta`
- a line in `.git/info/exclude` for that `.gitignore`

Both are local to your machine and neither can end up in a commit. The reason it takes two files
rather than one is that a Unity `.gitignore` normally ends with `![Aa]ssets/**/*.meta` so meta
files are never lost by accident, and a rule in `.gitignore` beats anything in
`.git/info/exclude` — so the folder would vanish and its `.meta` would not. Git ranks ignore files
by depth, so a `.gitignore` sitting beside this repository overrides the one at the project root.

### Updating

Because it is a plain clone, it is just `git pull` in this folder — or **Pull** in Fork with this
repository selected. Nothing in the project needs committing, and nothing pins a version, so you
get the latest whenever you pull.

### Contributing

Commit and push here as you would in any repository. Everyone else picks it up on their next pull.

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
