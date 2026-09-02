# The design language

Every tool here shares one look, held in `Editor/Design/ToolStyles.cs`. It was extracted from
EasyUpload once that window's design had settled, so what follows is a set of decisions that were
argued with in a real window rather than guessed at in the abstract.

The rules are the part worth keeping. The styles are only how they are enforced.

## The rules

**1. Two button levels, never three.** `Primary` is the one action a panel exists for; `Secondary`
is every other button. A third, quieter level was tried and removed: it read as *not clickable*
beside the buttons it sat next to, and being low-contrast already, it had nothing left to give up
when it needed to look disabled. Hierarchy belongs in *which* action is primary, not in making some
buttons whisper.

**2. A style that is not for a button gets its interactive states stripped.** See `Inert`.
`GUI.Label` asks its style to draw hovered whenever the pointer is inside its rect, so a label built
from `EditorStyles.label` lights up under the cursor and lies about being clickable.

**3. Disabled has to be visible.** See `DisabledScope`. IMGUI's own disabled tint is calibrated for
the built-in skin and barely shows through a custom background texture.

**4. Sizes come from the scale, never from a literal.** Spacing, control heights and button widths
are all named constants. If a new size is genuinely needed it belongs in `ToolStyles` with a name,
not inline in one window.

**5. A label that cannot wrap must be given a rect and elided into it.** A non-wrapping label
reports its content width as its *minimum* width, so a long path or ARN does not get clipped — it
pushes the window wider than the screen. Use `Elide`.

## Three habits no style can enforce

**Call `ToolStyles.Ensure()` first**, in every entry point that reads a style — a window's `OnGUI`,
an inspector's `OnInspectorGUI`, a drawer's `OnGUI`. The styles are built on demand and released on
every assembly reload, so anything that has not called it can be handed a null style, and IMGUI
answers a null style with a `NullReferenceException` from inside its own layout code. This has bitten
twice; both times it looked intermittent, because opening any tool window first made the problem go
away until the next reload.

**Set `wantsMouseMove` and repaint on `MouseMove`**, or hover states only appear when something else
happens to trigger a frame, and every button feels a tenth of a second behind the pointer.

**Freeze anything that decides whether a control exists, once per event pass.** IMGUI runs Layout
and Repaint over the same code and requires both to emit the same controls. A value a worker thread
can change between them throws *"Getting control N's position in a group with only M controls"* and
takes the window down. The convention is a `FreezeFrame()` at the top of `OnGUI` writing `frame*`
fields that the drawing then reads.

## The scale

| | |
| --- | --- |
| `SpaceXS` … `SpaceXL` | 2, 4, 6, 8, 12 |
| `ControlHeight` 20 | secondary buttons, popups, inline fields |
| `ActionHeight` 24 | the one action a panel is for |
| `ListRowHeight` 21 | a row in a virtualised list |
| `InRowHeight` 16 | a control inside a list row |
| `ButtonS` 56, `ButtonM` 84, `ButtonL` 132 | button widths |

## Putting a window together

```csharp
private void OnGUI()
{
    ToolStyles.Ensure();
    ToolStyles.Backdrop(position);
    if (Event.current.type == EventType.MouseMove) Repaint();

    FreezeFrame();

    GUILayout.Space(ToolStyles.SpaceL);
    using (new EditorGUILayout.HorizontalScope())
    {
        GUILayout.Space(ToolStyles.SpaceL);
        using (new EditorGUILayout.VerticalScope())
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("What this panel is for");
                // …
            }
        }
        GUILayout.Space(ToolStyles.SpaceL);
    }
    GUILayout.Space(ToolStyles.SpaceL);
}
```

Cards on a backdrop, one margin of backdrop around everything and between them, so the panels read
as panels rather than as slabs pushed against the window frame. `CardHeader(string)` is the default;
the numbered overload is only for panels that are steps taken in order — numbering panels that are
merely panels tells the reader to look for a sequence that is not there.

## Things learned the hard way

- `GUI.contentColor` *multiplies* a style's colour. Recolour through `ColouredLabel`, which sets
  every state, rather than tinting.
- `GUIStyle.none` has zero margin, which is rarely what you want between two controls.
- `EditorGUILayout.ScrollViewScope` unwinds on an exception; `BeginScrollView`/`EndScrollView` does
  not, so one throw inside leaves the whole editor GUI broken until a reload.
- `GUIUtility.ExitGUI()` after a structural change — adding or removing something the rest of the
  pass would have drawn.
- A `/` in a `GenericMenu` label silently becomes a submenu.

## This is not for UIElements

The panels inside the behaviour graph editor are UIElements, not IMGUI, and none of this applies to
them. They match the graph editor's own look instead — see `Editor/Templates/GraphPanels`.
