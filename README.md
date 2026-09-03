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

### Windows, under `Utilities/`

- **AssetBundle Viewer** — what is in a bundle, how big, and what it references.
- **Folder Structure Generator** — create a folder tree from a saved profile.
- **Sprite Editor** — 9-slice borders, cleanup and bleed, mask generation.
- **EasyUpload** — drag a build folder, review the diff against S3, upload.
- **Git Local Exclude Manager** — `.git/info/exclude` and skip-worktree, with a UI.
- **Material Extractor & Assigner** — pull TMP materials out and reassign them.
- **TMP Rich Text Builder** — compose and preview TMP rich text.

Below the separator, only in projects with the template assemblies:

- **Dynamic Template Bindings Settings** — what the bindings inspector shows.
- **Dynamic Template Bindings Builder** — add a run of binding keys from one pattern.

### In the Unity toolbar

Added beside the play controls, and present in every project:

- **Build Platform** — switch platform without opening Build Settings. Asks first, since a
  switch is a long reimport.
- **Backend Environment** — read and set the backend the client points at.
- **Timescale** — a slider for `Time.timeScale`, session-scoped so a debug value is not still
  0.1x tomorrow morning.
- **SRDebugger Cheats** — buttons, toggles and sliders for cheats you pick from the live
  SRDebugger screen, so the ones you use often are one click away rather than several menus deep.

The last three degrade quietly: each looks for the types it needs by name, so a project without
SRDebugger or the backend config simply does not show that control.

### On the right-click menu

Select a texture in the Project window, under **Sprite Editor**:

- **Open in Sprite Editor**
- **Create Masks**
- **Detect and Apply 9-Slice Borders**
- **Clear 9-Slice Borders**

The last three run on a whole selection at once, which is the point — they exist so you do not
have to open a window for twenty sprites.

Select anything, under **Git**:

- **Add to Local Exclude** / **Remove from Local Exclude**
- **Skip Worktree (keep local changes)** / **Stop Skipping Worktree**

### On the inspector

- A **`DynamicTemplateBindings`** component gets a box per issue, with buttons to add a key the
  graph uses but nothing declares, or remove one nothing uses; a right-click entry on any key
  finds the nodes using it; and reference counts and issue icons on the rows themselves.
  All of it is off until switched on in the settings window.
- A **`TemplateBehavior`** asset gets its node count, its node count including subgraphs, and how
  many of those nodes nothing calls — under the minimum version the inspector already showed.

### Inside the behaviour graph editor

On the toolbar, on the right:

- **Min Version** — which nodes decide the template's minimum client version, and what it would
  drop to without them.
- **Deprecated** — every deprecated node, and a button to swap each for its replacement, carrying
  the connections and field values across. What it cannot carry, it says so before you press, and
  lists afterwards.

### Quietly, without being asked

- The tools keep themselves out of the host project's git — see *It hides itself* above.
- Each tool registers its own settings file so those stay out of git too.
- The Folder Structure Generator hands folders to the QuickNavigation tool if the project has one,
  through reflection, so it costs nothing in projects that do not.

## Working on it

It is a submodule, so changes are committed and pushed here, then picked up elsewhere with
`git submodule update --remote`.

Before adding a tool:

- **[TOOLING.md](TOOLING.md)** — which assembly it belongs in, why a tool must never require a
  change to project code, editing assets safely, and how to compile-check without waiting for Unity.
- **[DESIGN.md](DESIGN.md)** — the shared look, the rules behind it, and the three habits that no
  style can enforce for you.
