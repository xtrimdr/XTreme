using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The others' mice in a live session (#319, asked by the user: "cursores como de mouse, con el color del colaborador,
/// que muestren específicamente lo que están tocando"; chosen with the user: the mouse over the timeline and what they
/// hold anywhere). Each window has its own size, zoom and scroll, so what goes is never pixels (<see cref="LivePointer"/>):
/// over the timeline, a row and a time (<see cref="TimelineView.PointerSpot"/>); holding a control in a mixer strip or
/// the inspector, whose it is and which control of that type it is in there. Each copy draws it where that is on its
/// own screen (<see cref="Controls.LivePointers"/>); a control it doesn't show (its mixer closed, another track in its
/// inspector) points at that row's header in the timeline. It goes with the presence, a few times a second at most;
/// only the pointer moving doesn't redo the tags (<see cref="LiveSession.PumpResult.PointersMoved"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>How long a control stays shown as held after the button is let go, so a quick click is seen too.</summary>
    const long HoldAfterMs = 700;
    /// <summary>Presence steps between two tries to find a control that wasn't found (~0.5 s).</summary>
    const int ResolveEvery = 15;

    /// <summary>What this copy holds now: whose and which control (null = nothing), and whether the button is still down.</summary>
    string? touchOwner, touchControl;
    bool touchDown;
    long touchUp;

    /// <summary>Someone's pointer and the control it points at on this screen, found again only when it changes.</summary>
    sealed class SeenPointer
    {
        public required string Who;
        public Avalonia.Media.Color Color;
        public LivePointer? Pointer;
        public Control? Target;
        public bool Resolved;
    }

    SeenPointer[] seenPointers = [];

    void InitLivePointers()
    {
        AddHandler(PointerPressedEvent, OnTouchPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnTouchReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    void OnTouchPressed(object? sender, PointerPressedEventArgs e)
    {
        if (live == null || e.Source is not Visual v) return;
        if (TouchOf(v) is { } t)
        {
            (touchOwner, touchControl) = t;
            touchDown = true;
        }
        else touchOwner = touchControl = null;
    }

    void OnTouchReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!touchDown) return;
        touchDown = false;
        touchUp = Environment.TickCount64;
    }

    /// <summary>What a press on <paramref name="v"/> holds: the control and the strip or panel it belongs to. Null over the timeline (it goes by its spot) or anything else.</summary>
    (string Owner, string Control)? TouchOf(Visual v)
    {
        Control? hit = null;
        for (Visual? x = v; x != null; x = x.GetVisualParent())
        {
            if (x == Timeline) return null;
            if (hit == null)
            {
                if (x is Control c && Touchable(c)) hit = c;
                continue;
            }
            if (x is Control box && BoxOwner(box) is { } own)
                return (own.Owner, $"{own.Area}:{hit.GetType().Name}:{IndexIn(box, hit)}");
        }
        return null;
    }

    /// <summary>What counts as a control someone holds: buttons, sliders, fields and the hand-drawn ones (faders, knobs, graphs), not their boxes.</summary>
    static bool Touchable(Control c) =>
        c is Button or Avalonia.Controls.Slider or TextBox or Fader or Knob
        || c.GetType().Namespace == typeof(Fader).Namespace && c is not Panel and not Decorator and not ContentControl;

    /// <summary>Whose a box is: a mixer strip (its track's, folder's or bus's id) or the inspector (what it shows).</summary>
    (string Area, string Owner)? BoxOwner(Control box)
    {
        if (box == Inspector) return InspectorOwner() is { } id ? ("insp", id) : null;
        foreach (var (id, card) in mixerLayout.Cards)
            if (card == box) return ("mix", id);
        return null;
    }

    /// <summary>What the inspector shows: the bus, the folder or the track.</summary>
    string? InspectorOwner() => busPanel.IsVisible ? selBusId : folderPanel.IsVisible ? SelectedFolder?.Id : sel?.Track.Id;

    /// <summary>Which control of its type <paramref name="hit"/> is in <paramref name="box"/>, in the tree's order (hidden ones count too, so both copies count alike).</summary>
    static int IndexIn(Control box, Control hit)
    {
        int n = 0;
        var type = hit.GetType();
        foreach (var d in box.GetVisualDescendants())
        {
            if (d == hit) return n;
            if (d.GetType() == type) n++;
        }
        return n;
    }

    /// <summary>This copy's pointer as it goes out: what it holds (and a little after letting go), or where it is over the timeline. Builds nothing while it is the same as <paramref name="last"/>.</summary>
    LivePointer? MyPointer(LivePointer? last)
    {
        bool holding = touchControl != null && (touchDown || Environment.TickCount64 - touchUp < HoldAfterMs);
        if (holding)
            return last is { Row: null } && last.Owner == touchOwner && last.Control == touchControl
                ? last : new LivePointer { Owner = touchOwner, Control = touchControl };
        if (!Timeline.PointerSpot(out string row, out double y, out double? sec, out double? head)) return null;
        // to the hundredth of the row and the millisecond: what moves less than that isn't sent again
        y = Math.Round(y, 2);
        sec = sec is { } s ? Math.Round(s, 3) : null;
        head = head is { } h ? Math.Round(h, 2) : null;
        return last is { Owner: null } && last.Row == row && last.RowY == y && last.Sec == sec && last.Head == head
            ? last : new LivePointer { Row = row, RowY = y, Sec = sec, Head = head };
    }

    /// <summary>Takes everyone's pointers as they are now (after a pump); the controls are found again only for those that changed.</summary>
    void TakePointers()
    {
        if (live is not { } s || s.Presence.Count == 0)
        {
            seenPointers = [];
            return;
        }
        var next = new SeenPointer[s.Presence.Count];
        int n = 0;
        foreach (var (who, pr) in s.Presence)
        {
            var was = Array.Find(seenPointers, p => p.Who == who);
            var p = was ?? new SeenPointer { Who = who };
            p.Color = Palette.PersonColor(Math.Max(0, s.ColorOf(who)));
            if (!Equals(p.Pointer, pr.Pointer)) p.Resolved = false;
            p.Pointer = pr.Pointer;
            next[n++] = p;
        }
        seenPointers = next;
    }

    /// <summary>Called on every presence step: puts each one's arrow where its pointer is on this screen.</summary>
    void StepPointers()
    {
        LivePointers.Begin();
        bool retry = presenceSteps % ResolveEvery == 0;
        foreach (var p in seenPointers)
        {
            LivePointers.Put(p.Who, p.Color, PointerAt(p, retry), p.Pointer is { Owner: not null });
        }
        LivePointers.End();
        LivePointers.Step();
    }

    /// <summary>Where someone's pointer is on this screen, in the arrows' layer; null = out of sight.</summary>
    Point? PointerAt(SeenPointer p, bool retry)
    {
        if (p.Pointer is not { } pt) return null;
        if (pt.Row != null)
            return Timeline.SpotPoint(pt.Row, pt.RowY, pt.Sec, pt.Head) is { } at ? Timeline.TranslatePoint(at, LivePointers) : null;
        if (pt.Owner == null || pt.Control == null) return null;
        // the control is found when the pointer changes, and again if it went away (another track in the inspector, the mixer closed)
        if (!p.Resolved || p.Target != null && !p.Target.IsEffectivelyVisible || p.Target == null && retry)
        {
            p.Target = FindControl(pt.Owner, pt.Control);
            p.Resolved = true;
        }
        if (p.Target is { IsEffectivelyVisible: true } c && c.Bounds.Width > 0)
            return c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), LivePointers);
        // not on this screen: the row's header in the timeline
        return Timeline.HeaderPoint(pt.Owner) is { } h ? Timeline.TranslatePoint(h, LivePointers) : null;
    }

    /// <summary>The control a pointer holds, as this copy shows it: in the same mixer strip or in the inspector when it shows the same thing; its box when the control isn't there.</summary>
    Control? FindControl(string owner, string control)
    {
        var parts = control.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[2], out int n)) return null;
        Control? box = parts[0] switch
        {
            "mix" when MixerHost.IsVisible && mixerLayout.Cards.TryGetValue(owner, out var card) => card,
            "insp" when InspectorBar.IsVisible && InspectorOwner() == owner => Inspector,
            _ => null,
        };
        if (box == null) return null;
        int k = 0;
        foreach (var d in box.GetVisualDescendants())
            if (d is Control c && c.GetType().Name == parts[1] && k++ == n) return c;
        return box;
    }

    void ClearPointers()
    {
        seenPointers = [];
        touchOwner = touchControl = null;
        touchDown = false;
        LivePointers.Clear();
    }
}
