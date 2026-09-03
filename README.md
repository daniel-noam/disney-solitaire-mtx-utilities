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
  graph uses but nothing declares, or remove one nothing uses; reference counts and issue icons on
  the rows themselves; and a right-click menu on any key offering *Show the nodes using this key*
  and *Rename key and graph references* (both described under the graph editor below).
  All of it is off until switched on in the settings window.
- A **`TemplateBehavior`** asset gets its node count, its node count including subgraphs, and how
  many of those nodes nothing calls — under the minimum version the inspector already showed.

### Inside the behaviour graph editor

Two toggles are added to the right-hand end of the graph editor's toolbar. Both panels start
hidden — the canvas is what you opened the window for — and each rebuilds when you switch it on
and from its own **Refresh** button, rather than every frame, because both walk every node of
every subgraph.

**Min Version** answers why a template asks for the client version it does. The version is shown
large, then the line the bare number cannot give you — *"Without the 3 at 1.23.0, it would be
1.13.0"* — because clearing the top tier buys nothing unless you clear all of it. Below that,
every node carrying a version, worst first. Clicking a row frames that node; if it lives inside a
subgraph the row says so and clicking opens *that* subgraph's window, not this one.

**Deprecated** lists every deprecated node in the graph and swaps each for its replacement:

- **Upgrade** builds the new node, copies the field values both versions share, rebuilds every
  connection on ports they share by name, and keeps the node's position, its tab, and its place
  in any group it belonged to. The view stays exactly where it was, with the new node selected.
- **Find** jumps to a node without changing it.
- Rows are colour-coded: white swaps cleanly, amber swaps but loses something, grey has no
  replacement to swap to. The tooltip names the exact ports and values at stake.
- A `~` marks a replacement worked out from the naming rather than declared by the node itself.
  Most deprecated nodes here never named a successor, so this is the common case — it is worth a
  glance before pressing.
- **Upgrade the clean ones** does the whole graph, but only the rows that lose nothing. Anything
  that would drop a connection stays behind for its own button.
- Anything that could not be reconnected is listed afterwards in the panel, with a **Find** to
  reach the node that was left unconnected, and repeated in the console. It is the one thing the
  graph itself cannot show you: once the old node is gone, its loose ends are invisible.

**Renaming a key rewrites the graph.** Right-click a key on the bindings component and choose
*Rename key and graph references*: it renames the binding and every node in the graph — and in its
subgraphs — that referenced the old name, in one pass. Renaming the binding alone is what produces
the *"used in the graph but missing from bindings"* errors, so the two halves are deliberately not
separable.

Two things it tells you before you commit to it:

- If the key shares a naming prefix with others, changing that prefix renames the whole family
  with it. The window says how many keys that is, because renaming half a family is worse than
  renaming none of it.
- Other graphs that use the same key — a badge graph paired with a popup, typically — are **not**
  updated. Nothing can see those from here, so they are yours to check.

The bindings inspector reaches in here too. *Show the nodes using this key* opens the graph and
types the key into the canvas's own search in **Values** mode, so the search box's next and
previous buttons walk the matches — better than selecting them all at once, which could only ever
show the ones that happened to share a tab.

### Quietly, without being asked

- The tools keep themselves out of the host project's git — see *It hides itself* above.
- Each tool registers its own settings file so those stay out of git too, and the Git Local
  Exclude Manager offers to add any it finds unregistered rather than waiting to be asked.
- EasyUpload reads the bucket list from the credentials you give it, so picking a destination is
  a list to choose from rather than a name to remember, and its *From build* button takes the
  folder straight from the MTX bundle build's output path.
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
