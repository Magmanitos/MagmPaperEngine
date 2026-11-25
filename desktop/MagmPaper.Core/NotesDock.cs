namespace MagmPaper.Core;

public enum NotesEdge { Left, Right, Top, Bottom }

public readonly record struct NotesDock(NotesEdge Edge, int Offset)
{
    public Area Place(Area owner, Area window, Area work, int gap)
    {
        int x = owner.Left + Offset;
        int y = owner.Top + Offset;
        switch (Edge)
        {
            case NotesEdge.Left: x = owner.Left - gap - window.Width; break;
            case NotesEdge.Right: x = owner.Right + gap; break;
            case NotesEdge.Top: y = owner.Top - gap - window.Height; break;
            case NotesEdge.Bottom: y = owner.Bottom + gap; break;
        }
        if (AlongY(Edge)) y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - window.Height));
        else x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - window.Width));
        return new(x, y, x + window.Width, y + window.Height);
    }

    public static NotesDock? Find(Area owner, Area window, Area work, int gap, int reach, int minimumOverlap)
    {
        NotesDock? best = null;
        int bestDistance = reach + 1;
        foreach (var edge in Enum.GetValues<NotesEdge>())
        {
            bool alongY = AlongY(edge);
            int overlap = alongY
                ? Math.Min(owner.Bottom, window.Bottom) - Math.Max(owner.Top, window.Top)
                : Math.Min(owner.Right, window.Right) - Math.Max(owner.Left, window.Left);
            int length = alongY ? Math.Min(owner.Height, window.Height) : Math.Min(owner.Width, window.Width);
            if (overlap < Math.Min(length, minimumOverlap)) continue;
            var dock = new NotesDock(edge, alongY ? window.Top - owner.Top : window.Left - owner.Left);
            var atEdge = dock.Place(owner, window, work, gap);
            if (atEdge.Left < work.Left || atEdge.Right > work.Right || atEdge.Top < work.Top || atEdge.Bottom > work.Bottom) continue;
            int distance = Math.Max(Math.Abs(atEdge.Left - window.Left), Math.Abs(atEdge.Top - window.Top));
            if (distance >= bestDistance) continue;
            best = dock;
            bestDistance = distance;
        }
        return best;
    }

    private static bool AlongY(NotesEdge edge) => edge is NotesEdge.Left or NotesEdge.Right;
}
