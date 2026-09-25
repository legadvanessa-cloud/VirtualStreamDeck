namespace VirtualStreamDeck.Core;

/// <summary>
/// Pure grid-placement logic: occupancy, free-slot search, move/swap and repair. Kept free of UI so
/// it is unit tested; the rule throughout is that no operation ever silently loses a button.
/// </summary>
public static class GridLayout
{
    public static readonly (int Cols, int Rows)[] Presets = { (3, 3), (4, 3), (5, 3), (5, 4), (6, 4), (8, 4) };
    public const int MinSize = 1;
    public const int MaxColumns = 12;
    public const int MaxRows = 8;

    public static bool[,] Occupancy(DeckPage page, int cols, int rows, string? excludeId = null)
    {
        var occ = new bool[rows, cols];
        foreach (var b in page.Buttons)
        {
            if (b.Id == excludeId) continue;
            for (var r = b.Row; r < b.Row + b.RowSpan; r++)
            for (var c = b.Col; c < b.Col + b.ColSpan; c++)
                if (r >= 0 && r < rows && c >= 0 && c < cols) occ[r, c] = true;
        }
        return occ;
    }

    public static bool Fits(DeckPage page, int cols, int rows, int row, int col, int rowSpan, int colSpan, string? excludeId = null)
    {
        if (row < 0 || col < 0 || rowSpan < 1 || colSpan < 1) return false;
        if (row + rowSpan > rows || col + colSpan > cols) return false;
        var occ = Occupancy(page, cols, rows, excludeId);
        for (var r = row; r < row + rowSpan; r++)
        for (var c = col; c < col + colSpan; c++)
            if (occ[r, c]) return false;
        return true;
    }

    public static (int Row, int Col)? FindFree(DeckPage page, int cols, int rows, int rowSpan = 1, int colSpan = 1, string? excludeId = null)
    {
        for (var r = 0; r <= rows - rowSpan; r++)
        for (var c = 0; c <= cols - colSpan; c++)
            if (Fits(page, cols, rows, r, c, rowSpan, colSpan, excludeId)) return (r, c);
        return null;
    }

    public static DeckButton? ButtonAt(DeckPage page, int row, int col) =>
        page.Buttons.FirstOrDefault(b => row >= b.Row && row < b.Row + b.RowSpan && col >= b.Col && col < b.Col + b.ColSpan);

    /// <summary>Moves a button within a page to (row, col). Fails (returns false) if it does not fit.</summary>
    public static bool Move(DeckPage page, int cols, int rows, DeckButton button, int row, int col)
    {
        if (!Fits(page, cols, rows, row, col, button.RowSpan, button.ColSpan, button.Id)) return false;
        button.Row = row;
        button.Col = col;
        return true;
    }

    /// <summary>Swaps two buttons on the same page. Both must fit in each other's anchor cell.</summary>
    public static bool Swap(DeckPage page, int cols, int rows, DeckButton a, DeckButton b)
    {
        if (a == b) return true;
        var (ar, ac, br, bc) = (a.Row, a.Col, b.Row, b.Col);
        // Test the swap against a page without either button.
        var probe = new DeckPage { Buttons = page.Buttons.Where(x => x != a && x != b).ToList() };
        if (!Fits(probe, cols, rows, br, bc, a.RowSpan, a.ColSpan)) return false;
        probe.Buttons.Add(new DeckButton { Row = br, Col = bc, RowSpan = a.RowSpan, ColSpan = a.ColSpan });
        if (!Fits(probe, cols, rows, ar, ac, b.RowSpan, b.ColSpan)) return false;
        (a.Row, a.Col, b.Row, b.Col) = (br, bc, ar, ac);
        return true;
    }

    /// <summary>
    /// Moves a button onto another page (possibly of another profile) at the first free slot.
    /// Returns false and leaves everything untouched when the target page is full.
    /// </summary>
    public static bool Transfer(DeckPage from, DeckPage to, int toCols, int toRows, DeckButton button)
    {
        var rs = Math.Min(button.RowSpan, toRows);
        var cs = Math.Min(button.ColSpan, toCols);
        var slot = FindFree(to, toCols, toRows, rs, cs) ?? FindFree(to, toCols, toRows);
        if (slot == null) return false;
        if (FindFree(to, toCols, toRows, rs, cs) == null) { rs = 1; cs = 1; }
        from.Buttons.Remove(button);
        button.Row = slot.Value.Row;
        button.Col = slot.Value.Col;
        button.RowSpan = rs;
        button.ColSpan = cs;
        to.Buttons.Add(button);
        return true;
    }

    /// <summary>
    /// Repairs a profile after load or after the grid shrank: clamps spans, and re-homes any button
    /// that overlaps another or falls outside the grid - onto a free cell of the same page, else onto
    /// a new page. Returns the number of buttons that had to move.
    /// </summary>
    public static int Repair(Profile profile)
    {
        profile.Columns = Math.Clamp(profile.Columns, MinSize, MaxColumns);
        profile.Rows = Math.Clamp(profile.Rows, MinSize, MaxRows);
        profile.Pages ??= new List<DeckPage>();
        if (profile.Pages.Count == 0) profile.Pages.Add(new DeckPage());
        int cols = profile.Columns, rows = profile.Rows, moved = 0;
        var homeless = new List<DeckButton>();
        var seenIds = new HashSet<string>();

        foreach (var page in profile.Pages)
        {
            page.Buttons ??= new List<DeckButton>();
            var kept = new List<DeckButton>();
            var probe = new DeckPage { Buttons = kept };
            foreach (var b in page.Buttons.Where(b => b != null))
            {
                if (string.IsNullOrEmpty(b.Id) || !seenIds.Add(b.Id)) b.Id = DeckButton.NewId();
                b.RowSpan = Math.Clamp(b.RowSpan, 1, rows);
                b.ColSpan = Math.Clamp(b.ColSpan, 1, cols);
                b.Icon ??= new IconSpec();
                b.Action ??= new ActionSpec();
                b.Action.Steps ??= new List<MacroStep>();
                b.States ??= new List<ButtonState>();
                foreach (var s in b.States) { s.Action ??= new ActionSpec(); s.Action.Steps ??= new List<MacroStep>(); }
                if (Fits(probe, cols, rows, b.Row, b.Col, b.RowSpan, b.ColSpan)) { kept.Add(b); continue; }
                homeless.Add(b);
            }
            page.Buttons = kept;
        }

        foreach (var b in homeless)
        {
            moved++;
            var placed = false;
            foreach (var page in profile.Pages)
            {
                var slot = FindFree(page, cols, rows, b.RowSpan, b.ColSpan) ?? FindFree(page, cols, rows);
                if (slot == null) continue;
                if (FindFree(page, cols, rows, b.RowSpan, b.ColSpan) == null) { b.RowSpan = 1; b.ColSpan = 1; }
                (b.Row, b.Col) = slot.Value;
                page.Buttons.Add(b);
                placed = true;
                break;
            }
            if (placed) continue;
            var np = new DeckPage();
            profile.Pages.Add(np);
            b.Row = 0; b.Col = 0; b.RowSpan = 1; b.ColSpan = 1;
            np.Buttons.Add(b);
        }
        return moved;
    }

    public static IEnumerable<(Profile Profile, DeckPage Page, DeckButton Button)> AllButtons(AppConfig cfg) =>
        from p in cfg.Profiles from pg in p.Pages from b in pg.Buttons select (p, pg, b);
}
