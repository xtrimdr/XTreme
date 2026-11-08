using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Who is where in a shared session (#291, asked by the user: "who is playing or touching what"). Each person has
/// the color the host gave them (<see cref="LiveSession.ColorOf"/>, <see cref="Palette.PersonColor"/>); their name, in
/// it, goes on the track and the clip they selected, on what they are editing (read from their changes, on the track,
/// the mixer strip or the clip) and on the track they record on or play with the keyboard; their playhead is a thin
/// line of their color while their transport runs. This copy says where it is from a timer (<see cref="PresenceTick"/>),
/// only when something changed, and the playhead only when it is not where it would be by now: a running one is drawn
/// on from the time it arrived. Nothing of it is saved or undone. Historial's steps and Biblioteca's takes say whose
/// they are with <see cref="PersonBadge"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>How often this copy looks at where it is (and the others' running playheads redraw).</summary>
    static readonly TimeSpan PresenceTick = TimeSpan.FromMilliseconds(33);
    /// <summary>How long the keyboard's track stays marked as played after its last note.</summary>
    const long PlayHoldMs = 1500;
    /// <summary>How far (s) this copy's running playhead may drift from where the others draw it before it is sent again.</summary>
    const double CursorDrift = 0.08;
    /// <summary>Marks the presence tag among a mixer strip's children.</summary>
    static readonly object PresencePillTag = new();

    DispatcherTimer? presenceTimer;
    /// <summary>Presence steps between two refreshes of the live dialog (~1 s).</summary>
    const int DialogEvery = 30;
    int presenceSteps;
    LivePresence? myPresence;
    long myPresenceStamp;
    bool presencePending;
    string? playedId;
    long playedAt;
    /// <summary>Who is on each id now (tracks, clips, folders, buses), for the mixer.</summary>
    Dictionary<string, TimelineView.PresenceTag> presenceTags = [];
    /// <summary>Each person's last presence and when it arrived, so a running playhead goes on from there.</summary>
    readonly Dictionary<string, (LivePresence Seen, long Stamp)> presenceSeen = [];

    void StartPresence()
    {
        if (presenceTimer == null)
        {
            presenceTimer = new DispatcherTimer { Interval = PresenceTick };
            presenceTimer.Tick += (_, _) => PresenceStep();
        }
        myPresence = null;
        presencePending = false;
        playedId = null;
        presenceSeen.Clear();
        presenceTimer.Start();
    }

    void StopPresence()
    {
        presenceTimer?.Stop();
        myPresence = null;
        presenceSeen.Clear();
        ApplyPresence();
        ClearPointers();
    }

    void PresenceStep()
    {
        if (live is not { } s) { presenceTimer!.Stop(); return; }
        ShareMine(s);
        // the dialog's latencies move on their own: about once a second while it is open
        if (++presenceSteps % DialogEvery == 0 && LiveAsk.IsVisible) RefreshLiveDialog();
        if (s.ExpireEditing()) ApplyPresence();
        if (Timeline.CursorsRolling) Timeline.InvalidateCursors();
        LivePulse.Step();
        StepPointers();
    }

    /// <summary>Where this copy is: built only when something changed, sent as soon as the session lets it.</summary>
    void ShareMine(LiveSession s)
    {
        string? track = sel?.Track.Id, clip = Timeline.SelectedClipId, playing = null;
        bool rec = false;
        if (RecordingTake && Timeline.RecordingLanes is { } lanes)
        {
            for (int i = 0; i < tracks.Count; i++)
                if (lanes.Contains(i)) { playing = tracks[i].Track.Id; rec = true; break; }
        }
        else
        {
            long now = Environment.TickCount64;
            if (keyTrack != null)
                for (int i = 0; i < tracks.Count; i++)
                    if (tracks[i].Track.Id == keyTrack)
                    {
                        if (tracks[i].Keys.AnySounding) { playedId = keyTrack; playedAt = now; }
                        break;
                    }
            if (playedId != null && now - playedAt < PlayHoldMs) playing = playedId;
            else playedId = null;
        }
        bool rolling = engine.Playing;
        double cursor = Timeline.Playhead;
        var last = myPresence;
        var pointer = MyPointer(last?.Pointer);
        bool same = last != null && last.Track == track && last.Clip == clip && last.Playing == playing && last.Recording == rec
            && last.Rolling == rolling && ReferenceEquals(last.Pointer, pointer)
            && (rolling ? Math.Abs(cursor - (last.Cursor + Stopwatch.GetElapsedTime(myPresenceStamp).TotalSeconds)) < CursorDrift : cursor == last.Cursor);
        if (!same)
        {
            myPresence = new LivePresence { Track = track, Clip = clip, Playing = playing, Recording = rec, Rolling = rolling, Cursor = cursor, Pointer = pointer };
            myPresenceStamp = Stopwatch.GetTimestamp();
            presencePending = true;
        }
        if (!same && last?.Still != myPresence!.Still) LivePulse.SetDoing(s.Name, DoingOf(myPresence!, false));
        if (presencePending) presencePending = !s.Share(myPresence!);
    }

    /// <summary>
    /// Puts everyone's tags where they are now (timeline and mixer): on each id the one doing the most there (editing,
    /// then playing or recording, then only selected), with "+n" when others are on it too.
    /// </summary>
    void ApplyPresence()
    {
        var on = new Dictionary<string, (string Who, int Rank, bool Rec, int More)>();
        var cursors = new List<TimelineView.PresenceCursor>();
        if (live is { } s)
        {
            void Put(string? id, string who, int rank, bool rec)
            {
                if (id == null) return;
                if (!on.TryGetValue(id, out var was)) { on[id] = (who, rank, rec, 0); return; }
                if (was.Who == who) { on[id] = (who, Math.Max(rank, was.Rank), rec || was.Rec, was.More); return; }
                on[id] = rank > was.Rank ? (who, rank, rec, was.More + 1) : was with { More = was.More + 1 };
            }
            foreach (var (who, pr) in s.Presence)
            {
                Put(pr.Track, who, 1, false);
                Put(pr.Clip, who, 1, false);
                Put(pr.Playing, who, 2, pr.Recording);
                bool known = presenceSeen.TryGetValue(who, out var seen);
                long stamp = known && ReferenceEquals(seen.Seen, pr) ? seen.Stamp : Stopwatch.GetTimestamp();
                // the button's orbs (#319): what each one does, and a flash when they pick something else
                LivePulse.SetDoing(who, DoingOf(pr, s.EditingBy(who).Count > 0));
                if (known && (seen.Seen.Track != pr.Track || seen.Seen.Clip != pr.Clip || seen.Seen.Playing != pr.Playing && pr.Playing != null)) LivePulse.Spark(who);
                presenceSeen[who] = (pr, stamp);
                if (pr.Rolling) cursors.Add(new(pr.Cursor, true, stamp, PersonTag(s, who, 0, false)));
            }
            foreach (var (who, ids) in s.Editing())
                foreach (var id in ids) Put(id, who, 3, false);
            foreach (var who in presenceSeen.Keys)
                if (!s.Presence.ContainsKey(who)) presenceSeen.Remove(who);
        }
        var tags = new Dictionary<string, TimelineView.PresenceTag>(on.Count);
        foreach (var (id, w) in on) tags[id] = PersonTag(live!, w.Who, w.More, w.Rec);
        presenceTags = tags;
        Timeline.SetPresence(tags, tags, [.. cursors]);
        ApplyMixerPresence();
        TakePointers();
    }

    /// <summary>What someone's orb on the live button shows (<see cref="Controls.LivePulse"/>).</summary>
    static Controls.LivePulse.Doing DoingOf(LivePresence pr, bool editing) =>
        pr.Recording ? Controls.LivePulse.Doing.Recording
        : pr.Playing != null || editing ? Controls.LivePulse.Doing.Active
        : pr.Rolling ? Controls.LivePulse.Doing.Rolling
        : Controls.LivePulse.Doing.Connected;

    static TimelineView.PresenceTag PersonTag(LiveSession s, string who, int more, bool rec) =>
        new(more > 0 ? $"{who} +{more}" : who, Palette.PersonColor(Math.Max(0, s.ColorOf(who))), rec);

    /// <summary>The tags on the mixer's strips: over the head of each strip someone is on, never taking a click.</summary>
    void ApplyMixerPresence()
    {
        foreach (var (id, card) in mixerLayout.Cards)
        {
            if (card is not Border { Child: Panel panel }) continue;
            Border? pill = null;
            foreach (var c in panel.Children)
                if (c is Border b && b.Tag == PresencePillTag) { pill = b; break; }
            if (!presenceTags.TryGetValue(id, out var tag))
            {
                if (pill != null) panel.Children.Remove(pill);
                continue;
            }
            if (pill == null)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                row.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Palette.Rec, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { FontSize = 9, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                pill = new Border
                {
                    Tag = PresencePillTag, IsHitTestVisible = false, Child = row, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(4, 1, 4, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                };
                panel.Children.Add(pill);
            }
            var r = (StackPanel)pill.Child!;
            r.Children[0].IsVisible = tag.Recording;
            var text = (TextBlock)r.Children[1];
            text.Text = tag.Text;
            text.Foreground = Palette.Alpha(tag.Color, 1);
            // over the strip's own colors: the panel's, tinted with the person's
            pill.Background = Palette.Alpha(Color.FromArgb(255,
                (byte)((Palette.PanelColor.R * 3 + tag.Color.R) / 4), (byte)((Palette.PanelColor.G * 3 + tag.Color.G) / 4), (byte)((Palette.PanelColor.B * 3 + tag.Color.B) / 4)), 1);
            pill.BorderBrush = Palette.Alpha(tag.Color, 0.9);
        }
    }

    /// <summary>
    /// A person's name as a badge (Historial's steps, Biblioteca's takes): in their color while they are in the
    /// session, plain when they aren't.
    /// </summary>
    Border PersonBadge(string who)
    {
        int c = live?.ColorOf(who) ?? -1;
        Color? color = c >= 0 ? Palette.PersonColor(c) : null;
        var fg = color is { } k ? Palette.Alpha(k, 1) : Palette.Faint;
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        label.Children.Add(HistoryIcon("", 9, fg));
        label.Children.Add(new TextBlock { Text = who, FontSize = 10, FontWeight = FontWeight.Medium, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120 });
        return new Border
        {
            Background = color is { } bg ? Palette.Alpha(bg, 0.16) : Brushes.Transparent, BorderBrush = color is { } line ? Palette.Alpha(line, 0.5) : Palette.Line,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 0, 5, 1), Child = label,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }
}
