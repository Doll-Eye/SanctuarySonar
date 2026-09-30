// Objective.cs — the C# port of Sources/Cartography/Objective.swift. See Core.cs for the
// rules of the port: same logic, same constants, same names in the same case.
//
// PORT NOTE: Swift Characters are grapheme clusters and `String.count` counts them; this
// port works on C# UTF-16 chars and `Length`. Swift's `Character.isLetter` / `isNumber` /
// `isUppercase` are `char.IsLetter` / `char.IsDigit` / `char.IsUpper` here (`isNumber` is the
// wider set in Swift: fractions, Roman numerals). `lowercased()` / `uppercased()` are full
// Unicode case mappings in Swift and simple ones here (`ToLowerInvariant` / `ToUpperInvariant`;
// "ß" upper-cases to "SS" in Swift and stays "ß" here). String equality is ordinal here and
// canonical-equivalence based in Swift. Recogniser text has not shown a difference under any
// of these; a combining mark or an emoji in a line would.
using System.Globalization;
using System.Text.RegularExpressions;

namespace SanctuarySonar.Cartography;

/// <summary>
/// Where the objective tracker is: the column of text under the minimap, right-aligned.
/// Measured on the 25 Sep 2026 Shadow recording (2646×1663 picture): from just under the
/// minimap (y ≈ 470 in the window) down about 600 px, the right 700 px. Same assumption as
/// <c>MinimapLayout</c>: the HUD scales with the picture's height. Unverified elsewhere.
/// </summary>
public static class TrackerLayout
{
    internal const double referenceHeight = 1663.0;
    internal const double rightInset = 20.0 / referenceHeight;
    internal const double width = 700.0 / referenceHeight;
    internal const double top = 413.0 / referenceHeight;
    internal const double height = 600.0 / referenceHeight;

    public static (int x, int y, int width, int height) rect(int windowWidth, int windowHeight, int titleBar)
    {
        double pictureHeight = (double)(windowHeight - titleBar);
        int w = (int)(width * pictureHeight), h = (int)(height * pictureHeight);
        int x = windowWidth - (int)(rightInset * pictureHeight) - w;
        int y = titleBar + (int)(top * pictureHeight);
        return (Math.Max(0, x), Math.Max(0, y), Math.Min(w, windowWidth), Math.Min(h, windowHeight - y));
    }

    /// <summary>
    /// The whole right-hand HUD column from the top of the picture to the tracker's foot:
    /// the area name ("Path of Blood | 4:26 AM", picture y 0–70 at 1663), the minimap, and
    /// the tracker. Read in one pass and split by height with <c>areaBand</c> and <c>trackerTop</c>.
    /// </summary>
    public static (int x, int y, int width, int height) hudRect(int windowWidth, int windowHeight, int titleBar)
    {
        double pictureHeight = (double)(windowHeight - titleBar);
        int w = (int)(width * pictureHeight), h = (int)((top + height) * pictureHeight);
        int x = windowWidth - (int)(rightInset * pictureHeight) - w;
        return (Math.Max(0, x), titleBar, Math.Min(w, windowWidth), Math.Min(h, windowHeight - titleBar));
    }

    /// <summary>Fractions of the HUD column's height.</summary>
    public const double areaBand = 70.0 / (413.0 + 600.0);
    public const double trackerTop = 413.0 / (413.0 + 600.0);
}

/// <summary>The area the player is in, from the line above the minimap: "Ghastly Depths © | 4:45 AM".</summary>
public static class AreaName
{
    public static string? parse(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            // components(separatedBy:) always yields at least one piece, as Split does.
            string text = line.Split('|')[0];
            // PORT NOTE: ICU's \d and \s (Swift) and .NET's are both Unicode classes; the same here.
            text = Regex.Replace(text, @"\d{1,2}:\d{2}\s*(AM|PM)?", "");
            // Words in capitals are the interface ("TAB", or a menu's "GAME" and "SHOP"); area
            // names are in title case.
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !(w.Length >= 2 && w == w.ToUpperInvariant() && w.Any(char.IsLetter)));
            string name = Objective.clean(SwiftStr.trimWhitespaces(string.Join(" ", words)));
            bool titled = name.Length >= 4 && char.IsUpper(name[0])
                && name.All(c => char.IsLetter(c) || c == ' ' || c == '\'' || c == '’' || c == '-');
            if (titled) return name;
        }
        return null;
    }
}

/// <summary>
/// What the tracker says about the dungeon, read from its lines.
/// The tracker lists quests top to bottom, each a title line followed by its objective
/// lines. Seen in Forbidden City: "Normal" (the difficulty), "Forbidden City", "Travel to the
/// Tomb of Thazbach", "Nightmare Dungeon", then the seasonal quest "Conquer a Nightmare
/// Dungeon" with its own lines. The dungeon is the first block, so its title is the first
/// line that is not the difficulty, and its objective is the line after.
/// </summary>
/// <param name="place">The dungeon's name, the title line above the objective.</param>
/// <param name="text">The objective line, as read.</param>
public readonly record struct Objective(string place, string text)
{
    /// <summary>
    /// Whether the place reads like a title — words of letters, as dungeon names are. A
    /// banner's fragment ("TION.") is not.
    /// </summary>
    public bool looksLikeTitle =>
        place.Length >= 4 && char.IsUpper(place[0])
            && place.All(c => char.IsLetter(c) || c == ' ' || c == '\'' || c == '’' || c == '-');

    /// <summary>The objective with counts removed, so "Slay the Enraged Spirits: 2" and "…: 1" are
    /// the same objective progressing, not a new one.</summary>
    public string kind => split(text).kind;
    /// <summary>The count at the end of the line ("…: 3", "…: 2/5"), if there is one. The tracker's
    /// green "1" is read as "l" or "I" as often as not.</summary>
    public int? count => split(text).count;

    /// <summary>The objective's words and its count, apart.</summary>
    internal static (string kind, int? count) split(string text)
    {
        char[] edges = " :•◆·,.-'\"¥+*".ToCharArray();
        int colon = text.LastIndexOf(':');
        if (colon < 0)
        {
            // No colon: a short number on its own at the end is still a count.
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string last = parts[^1];
                if (last.Length <= 2 && SwiftStr.toInt(last) is int n)
                    return (text.Substring(0, text.Length - last.Length).Trim(edges), n);
            }
            return (text.Trim(edges), null);
        }
        string tail = text.Substring(colon + 1);
        string letters = SwiftStr.letters(tail);
        // A count clause holds digits, or a lone l/I standing for 1, and no other letters.
        if (!(letters.Length == 0 || letters == "l" || letters == "I" || letters == "i"))
            return (text.Trim(edges), null);
        string[] slashed = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string digits = SwiftStr.digits(slashed.Length > 0 ? slashed[0] : "");
        int? count = SwiftStr.toInt(digits) ?? (letters.Length == 0 ? (int?)null : 1);
        return (text.Substring(0, colon).Trim(edges), count);
    }

    /// <summary>
    /// Whether two readings are the same words, give or take a few letters — two, or a
    /// fifth of the letters, whichever is more. "Seaborn Goddess" was read as "Seabom
    /// Gpddess", "Seabom'Goildess" and "Godde3s" on 26 Sep, each a new objective at a
    /// fixed tolerance of two.
    /// </summary>
    public static bool alike(string a, string b)
    {
        static string squash(string s) => new string(s.ToLowerInvariant().Where(c => char.IsLetter(c) || char.IsDigit(c)).ToArray());
        string x = squash(a), y = squash(b);
        return x == y || HUDState.distance(x, y) <= Math.Max(2, Math.Min(x.Length, y.Length) / 5);
    }

    internal static readonly HashSet<string> difficulties = new(StringComparer.Ordinal)
    {
        "normal", "hard", "expert", "penitent", "torment",
        "torment i", "torment ii", "torment iii", "torment iv"
    };

    /// <summary>
    /// Words an objective line starts with. The objective is found by what it says, and
    /// the place is the line above it: taking "the first two lines" let one stray fragment
    /// above the dungeon's name make "Forbidden City" the objective (233 readings on the
    /// 26 Sep replay with the fast recogniser).
    /// </summary>
    internal static readonly string[] verbs =
    {
        "slay", "kill", "defeat", "destroy", "travel", "enter", "go ", "reach", "collect", "gather",
        "free", "rescue", "find", "activate", "open", "use", "return", "bring", "carry", "place",
        "deposit", "survive", "protect", "escort", "cleanse", "search", "explore", "investigate",
        "dungeon cleared", "complete", "light", "ring", "speak", "interact", "pull", "break",
        "purge", "escape", "loot", "obtain", "retrieve", "recover", "deliver", "assist", "help",
        "hunt", "banish", "capture", "close", "seal", "summon", "confront", "ascend", "descend",
        "follow", "locate", "discover", "clear", "kindle", "extinguish", "release", "restore"
    };

    public static Objective? parse(IEnumerable<string> lines)
    {
        List<string> useful = lines.Select(SwiftStr.trimWhitespaces)
            .Where(l => clean(l).Length > 2 && !difficulties.Contains(clean(l).ToLowerInvariant()))
            .ToList();
        if (useful.Count < 2) return null;
        // PORT NOTE: `CharacterSet.letters.inverted` — letters and marks are kept, see SwiftStr.inLetterSet.
        static string stripped(string s) => SwiftStr.trimNonLetters(s.ToLowerInvariant());
        // Only a line that starts with a verb is an objective. The old fallback to "the first
        // two lines" turned every corrupted reading ("Goddess: 3 '•*", "' estroy the…") into a
        // new objective without a verb, flipping the guide between enemies and exploring on
        // the 26 Sep Mariner's Refuge run — 107 flip-flops in 41 minutes.
        // …and only the line right under the dungeon's name: the seasonal quest lower down
        // ("Slay Harbingers") was read as the objective whenever the dungeon's own line was
        // unreadable in a frame.
        // The objective is the first verb line in the dungeon's block: right under its name
        // as a rule, but the Undercity puts its tier bar and rewards between ("Reward
        // Upgrades (0/4)", "Attunement: …", "Time Bonus: …", then "Reach District Boss
        // before time expires" — 27 Sep 2026, read as nothing for a whole run).
        if (useful.Count < 2) return null;
        int? found = null;
        for (int n = 1; n < Math.Min(useful.Count, 6); n++)
        {
            if (verbs.Any(v => stripped(useful[n]).StartsWith(v, StringComparison.Ordinal))) { found = n; break; }
        }
        if (found is not int i) return null;
        // Its place is the nearest title-looking line above it — a quest lower down has its
        // own title, and a place that changes is believed only after five readings.
        bool titleLike(string line)
        {
            // On the raw line: "Reward Upgrades (0/4)" is a count, not a title (it was the
            // Undercity objective's place on 27 Sep).
            string t = SwiftStr.trimWhitespaces(line);
            // …and a line that starts with a verb is an objective, not a place: "Purge the
            // Kurast Undercity" over "Descend into the Undercity…" was read as place and
            // objective before the dungeon's own block appeared (27 Sep, 07:46).
            return t.Length >= 4 && char.IsUpper(t[0]) && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
                && t.All(c => char.IsLetter(c) || c == ' ' || c == '\'' || c == '’' || c == '-')
                && !verbs.Any(v => stripped(t).StartsWith(v, StringComparison.Ordinal));
        }
        int placeLine = 0;
        for (int k = i - 1; k >= 1; k--)
        {
            if (titleLike(useful[k])) { placeLine = k; break; }
        }
        if (!(titleLike(useful[placeLine]) || placeLine == 0 && !verbs.Any(v => stripped(useful[0]).StartsWith(v, StringComparison.Ordinal)))) return null;
        {
            // Whatever was read before the verb ("1;; Destroy", "4¥\" Destroy") is not words.
            string text = SwiftStr.dropLeadingNonLetters(useful[i]).Trim(" •◆·,.-'¥\"".ToCharArray());
            // A tick after "Dungeon cleared" reads as a stray letter ("v", "V"), and each
            // change of case was a new objective. A trailing single letter is never a word.
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string last = parts[^1];
                if (last.Length == 1 && char.IsLetter(last[0]))
                    text = SwiftStr.trimWhitespaces(text.Substring(0, text.Length - 1));
            }
            return new Objective(clean(useful[placeLine]), text);
        }
    }

    /// <summary>
    /// The icon beside a quest title is read as a stray token or two ("Forbidden City fI",
    /// "Forbidden City 11)"): drop trailing tokens of two characters or fewer, or any that
    /// hold no letter. Titles only — see <c>parse</c>.
    /// </summary>
    public static string clean(string line)
    {
        List<string> tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1)
        {
            string last = tokens[^1];
            if (last.Length <= 2 || !last.Any(SwiftStr.inLetterSet)
                || last.All(c => "Il1|!".Contains(c))) tokens.RemoveAt(tokens.Count - 1);
            else break;
        }
        return SwiftStr.trimWhitespaces(string.Join(" ", tokens));
    }

    // ---- public extension Objective ----

    public static readonly string[] slayWords = { "slay", "kill", "defeat", "destroy" };
    public static readonly string[] travelWords = { "travel to", "enter ", "go to", "reach " };

    /// <summary>
    /// <c>currentArea</c> is the name above the minimap. On 26 Sep: "Slay all enemies in the
    /// Ghastly Depths" in the Ghastly Depths → slay, stay; "Travel to the Tomb of Thazbach"
    /// in the Ghastly Depths → travel, leave the Ghastly Depths.
    /// </summary>
    /// <summary>`timed` is what the HUD says about the run (HUDState.timedRun): the place alone
    /// was not enough (29 Sep 2026: the place read "Monsters", the run counted as untimed, the
    /// objective marker was never looked for).</summary>
    public Intent intent(string? currentArea, bool timed = false)
    {
        string lower = kind.ToLowerInvariant();
        bool slay = slayWords.Any(w => lower.Contains(w, StringComparison.Ordinal));
        bool travel = travelWords.Any(w => lower.Contains(w, StringComparison.Ordinal));
        string? stayIn = null, leave = null, destination = null;
        if (travel)
        {
            // The first travel word (in list order) found in `kind`, case-insensitively.
            int upperBound = -1;
            foreach (string w in travelWords)
            {
                int at = kind.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) { upperBound = at + w.Length; break; }
            }
            if (upperBound >= 0)
            {
                string named = kind.Substring(upperBound).Trim(" ().".ToCharArray());
                if (named.ToLowerInvariant().StartsWith("the ", StringComparison.Ordinal)) named = named.Substring(4);
                // "Reach District Boss before time expires": the place ends at the condition.
                foreach (string cut in new[] { " before ", " within ", " while ", " and ", " to " })
                {
                    int r = named.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
                    if (r >= 0) named = named.Substring(0, r);
                }
                if (named.Length >= 3) destination = named;
            }
        }
        if (currentArea != null)
        {
            string here = currentArea.ToLowerInvariant();
            int range = lower.IndexOf(" in the ", StringComparison.Ordinal);
            int rangeLength = " in the ".Length;
            if (range < 0) { range = lower.IndexOf(" in ", StringComparison.Ordinal); rangeLength = " in ".Length; }
            if (range >= 0)
            {
                string named = lower.Substring(range + rangeLength).Trim(" ().".ToCharArray());
                // PORT NOTE: with an empty `here`, C#'s Contains("") is true; Swift's answer for
                // an empty argument is not relied on by any caller (the area is nil or a name).
                if (named.Length > 0 && (named.Contains(here, StringComparison.Ordinal) || here.Contains(named, StringComparison.Ordinal))) stayIn = currentArea;
            }
            // "Reach …" names a thing (the district boss, the exit), not a place to travel to:
            // no area to leave for it. "Travel to" and "Enter" name places.
            if (travel && !lower.StartsWith("reach", StringComparison.Ordinal))
            {
                string target = destination?.ToLowerInvariant() ?? lower;
                if (!target.Contains(here, StringComparison.Ordinal) && !here.Contains(target, StringComparison.Ordinal)) leave = currentArea;
            }
        }
        return new Intent(slay: slay, travel: travel, stayIn: stayIn, leave: leave, destination: destination,
                          timed: timed || place.ToLowerInvariant().Contains("undercity", StringComparison.Ordinal));
    }
}

// TrackerReader (Vision OCR) is platform code; the Windows shell supplies text lines.

/// <summary>What an objective asks of the player, as far as where to go is concerned.</summary>
public readonly record struct Intent
{
    /// <summary>Something to kill: lead to red marks.</summary>
    public bool slay { get; }
    /// <summary>Somewhere to reach: lead to arches not yet used.</summary>
    public bool travel { get; }
    /// <summary>"… in the &lt;area&gt;" while in that area: stay in it.</summary>
    public string? stayIn { get; }
    /// <summary>Travelling to somewhere that is not here: leave this area.</summary>
    public string? leave { get; }
    /// <summary>Where a travel objective goes ("Travel to the Siren's Chamber" → "Siren's Chamber"),
    /// so that a place already walked through can be led to directly.</summary>
    public string? destination { get; }
    /// <summary>A timed run (the Kurast Undercity): the objective marker is the way, side rooms cost
    /// time, and the well-and-arch heuristics of an ordinary dungeon do not apply.</summary>
    public bool timed { get; }

    public Intent(bool slay, bool travel, string? stayIn, string? leave, string? destination = null, bool timed = false)
    {
        this.slay = slay; this.travel = travel; this.stayIn = stayIn; this.leave = leave; this.destination = destination; this.timed = timed;
    }

    public static readonly Intent none = new Intent(false, false, null, null);
}

/// <summary>
/// Believes what the HUD says only when it says it steadily — shared by the live guide and
/// MapLab so both behave the same. Areas: a reading cut short ("Path of Blo", "Ghast") is the
/// known area it begins; a new area must be read three times running. Objectives: two agreeing
/// readings for the same place, five if the place changed, and never while the minimap is
/// covered (the caller passes only readings taken while it is legible).
/// </summary>
public sealed class HUDState
{
    public string? area { get; private set; }
    public Objective? objective { get; private set; }
    /// <summary>The objective's count as last confirmed (two readings running), if it has one.</summary>
    public int? count { get; private set; }
    /// <summary>A countdown shown in the HUD column between the minimap and the tracker — the
    /// Undercity's timer (27 Sep 2026: "150" under "FLOOR 1/3") — as last confirmed.</summary>
    public int? timer { get; private set; }
    private (int value, int reads)? timerCandidate;
    /// <summary>The floor counter under "FLOOR" in the same band ("1/3"): floor and how many.</summary>
    public (int number, int of)? floor { get; private set; }
    private (int value, int reads)? floorCandidate;
    // PORT NOTE: Swift's `public private(set) var knownAreas: [String]` is a value; a List is
    // shared by reference. Readers must not mutate it.
    public List<string> knownAreas { get; private set; } = new List<string>();
    private (string name, int count)? areaCandidate;
    private (Objective objective, int count)? objectiveCandidate;
    private (int value, int reads)? countCandidate;
    /// <summary>How often each spelling of the current objective has been read: the one read most
    /// is the one kept, so "Seabom" gives way to "Seaborn" as the readings come in.</summary>
    private Dictionary<string, (int reads, string text)> spellings = new Dictionary<string, (int reads, string text)>(StringComparer.Ordinal);

    public HUDState() { }

    /// <summary>The tracker has said "Undercity" somewhere (the "Kurast Undercity" header line):
    /// a timed run, whatever the objective's place reads as.</summary>
    public bool timedRun { get; private set; }

    public void reset()
    {
        area = null; objective = null; count = null; timer = null; floor = null; timedRun = false;
        areaCandidate = null; objectiveCandidate = null; countCandidate = null; timerCandidate = null; floorCandidate = null;
        spellings = new Dictionary<string, (int reads, string text)>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns what changed: the area, the objective (a different one, not a better reading
    /// of the same), and the count when it moves after the objective was first read.
    /// PORT NOTE: five elements, as the Swift — `timer` sits between `count` and `floor`.
    /// </summary>
    public (string? area, Objective? objective, int? count, int? timer, (int number, int of)? floor) update(IReadOnlyList<(string text, double y)> lines)
    {
        string? newArea = null;
        if (!timedRun && lines.Any(l => l.text.ToLowerInvariant().Contains("undercity", StringComparison.Ordinal))) timedRun = true;
        // The floor counter: "1/3" in the band under the minimap, two readings running.
        (int number, int of)? newFloor = null;
        {
            string? text = lines.Where(l => l.y >= TrackerLayout.areaBand && l.y < TrackerLayout.trackerTop)
                .Select(l => SwiftStr.trimWhitespaces(l.text))
                .FirstOrDefault(t => t.Length <= 5 && t.Contains('/') && t.All(c => char.IsDigit(c) || c == '/'));
            if (text != null)
            {
                int slash = text.IndexOf('/');
                if (slash >= 0 && SwiftStr.toInt(text.Substring(0, slash)) is int n
                    && SwiftStr.toInt(text.Substring(slash + 1)) is int of && of >= n && n >= 1)
                {
                    int reads = floorCandidate?.value == n ? (floorCandidate?.reads ?? 0) + 1 : 1;
                    floorCandidate = (n, reads);
                    if (reads >= 2 && n != floor?.number) { floor = (n, of); newFloor = (n, of); }
                }
            }
        }
        // The timer: a bare number of up to three digits in the band under the minimap, two
        // readings running within a few seconds of each other (it counts down).
        int? newTimer = null;
        {
            // The lowest such number on screen: the floor counter "1/3" sits above the timer
            // and was read as "173" for eight seconds straight (29 Sep 2026).
            string? digits = lines.Where(l => l.y >= TrackerLayout.areaBand && l.y < TrackerLayout.trackerTop)
                .Select(l => (text: SwiftStr.trimWhitespaces(l.text), y: l.y))
                .Where(t => t.text.Length >= 1 && t.text.Length <= 3 && t.text.All(c => char.IsDigit(c)))
                .OrderByDescending(t => t.y).Select(t => (string?)t.text).FirstOrDefault();
            if (digits != null && SwiftStr.toInt(digits) is int value)
            {
                int reads = (timerCandidate.HasValue && Math.Abs(timerCandidate.Value.value - value) <= 3) ? (timerCandidate?.reads ?? 0) + 1 : 1;
                timerCandidate = (value, reads);
                // A jump up needs three readings: "213" was read twice during a floor change
                // (27 Sep, 08:03) and announced as time bought.
                // A jump up needs three readings; a jump of more than a minute needs six
                // ("173" read three times at 26 s left, 29 Sep 2026).
                int jump = timer.HasValue ? value - timer.Value : 0;
                int needed = jump > 60 ? 6 : jump > 3 ? 3 : 2;
                if (reads >= needed && value != timer) { timer = value; newTimer = value; }
            }
        }
        string? read = AreaName.parse(lines.Where(l => l.y < TrackerLayout.areaBand).Select(l => l.text));
        if (read != null)
        {
            static string squash(string s) => new string(s.ToLowerInvariant().Where(c => char.IsLetter(c)).ToArray());
            string r = squash(read);
            // The same area if it matches a known name, begins it (read cut short), or is
            // within a few letters of it ("Gh stly Depths"; "Hollow Cavims" for Hollow
            // Caverns is three edits, and became a third area on 26 Sep) — two, or a fifth
            // of the name's letters, whichever is more.
            // …or is a piece of it ("District", "C ve District" for Cave District under the
            // fast recogniser, 27 Sep — three areas for one, and the intent flipped with them).
            string? known = knownAreas.FirstOrDefault(k => squash(k) == r)
                ?? (r.Length >= 4 ? knownAreas.FirstOrDefault(k => squash(k).StartsWith(r, StringComparison.Ordinal)) : null)
                ?? (r.Length >= 6 ? knownAreas.FirstOrDefault(k => squash(k).Contains(r, StringComparison.Ordinal)) : null)
                ?? (r.Length >= 6 ? knownAreas.FirstOrDefault(k => distance(squash(k), r) <= Math.Max(2, r.Length / 5)) : null);
            // A new area is a name of six letters or more, read the same three times.
            if (known != null || r.Length >= 6)
            {
                string name = known ?? read;
                int count = areaCandidate?.name == name ? (areaCandidate?.count ?? 0) + 1 : 1;
                areaCandidate = (name, count);
                if (name != area && count >= (known != null ? 2 : 3))
                {
                    area = name;
                    if (known == null) knownAreas.Add(name);
                    newArea = name;
                }
            }
        }
        var (newObjective, newCount) = objectiveUpdate(lines);
        return (newArea, newObjective, newCount, newTimer, newFloor);
    }

    private (Objective?, int?) objectiveUpdate(IReadOnlyList<(string text, double y)> lines)
    {
        Objective? parsed = Objective.parse(lines.Where(l => l.y >= TrackerLayout.trackerTop).Select(l => l.text));
        if (parsed is not Objective read || !read.looksLikeTitle
            // An objective that is the dungeon's own name is the tracker shifted by a line.
            || string.Equals(read.text, objective?.place ?? "", StringComparison.OrdinalIgnoreCase)) return (null, null);
        // "Tomb ofThazbach" / "Tomb of Thazbach", "Seaborn" / "Seabom" / "Gpddess": the same
        // words within a few letters are the same objective and the same place.
        static bool same(Objective? a, Objective b)
        {
            if (a is not Objective x) return false;
            return Objective.alike(x.kind, b.kind) && Objective.alike(x.place, b.place);
        }
        Objective? newObjective = null;
        if (objective is Objective current && same(current, read))
        {
            objectiveCandidate = null;
            (int reads, string text) entry = spellings.TryGetValue(read.kind, out var existing) ? existing : (0, read.text);
            entry.reads += 1; entry.text = read.text;
            if (spellings.Count < 40 || spellings.ContainsKey(read.kind)) spellings[read.kind] = entry;
            if (entry.reads > (spellings.TryGetValue(current.kind, out var kept) ? kept.reads : 0))
            {
                objective = new Objective(current.place, read.text);
            }
        }
        else
        {
            int count = same(objectiveCandidate?.objective, read) ? (objectiveCandidate?.count ?? 0) + 1 : 1;
            objectiveCandidate = (read, count);
            int needed = (objective == null || Objective.alike(objective?.place ?? "", read.place)) ? 2 : 5;
            if (count < needed) return (null, null);
            objective = read;
            objectiveCandidate = null;
            spellings = new Dictionary<string, (int reads, string text)>(StringComparer.Ordinal) { [read.kind] = (count, read.text) };
            this.count = null;
            countCandidate = null;
            newObjective = read;
        }
        // The count, believed after two readings running; the first is part of the
        // objective as announced, and only a move after that is progress.
        int? newCount = null;
        if (read.count is int value)
        {
            int reads = countCandidate?.value == value ? (countCandidate?.reads ?? 0) + 1 : 1;
            countCandidate = (value, reads);
            if (reads >= 2 && value != this.count)
            {
                if (this.count != null) newCount = value;
                this.count = value;
            }
        }
        return (newObjective, newCount);
    }

    /// <summary>Edit distance, for names read with a letter or two wrong.
    /// PORT NOTE: over UTF-16 chars; the Swift is over Characters.</summary>
    internal static int distance(string a, string b)
    {
        char[] aa = a.ToCharArray(), bb = b.ToCharArray();
        if (aa.Length == 0) return bb.Length;
        int[] row = Enumerable.Range(0, bb.Length + 1).ToArray();
        for (int i = 1; i <= aa.Length; i++)
        {
            int previous = row[0]; row[0] = i;
            for (int j = 1; j <= Math.Max(bb.Length, 1); j++)
            {
                if (!(j <= bb.Length)) continue;
                int keep = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (aa[i - 1] == bb[j - 1] ? 0 : 1));
                previous = keep;
            }
        }
        return row[bb.Length];
    }
}

/// <summary>
/// The full dungeon map, opened with Start: recognised by its bottom bar of button prompts
/// ("Open Party Finder · Center on Player · Pan · Zoom · Pin Location · Close") and read
/// for the area list in its left panel, under the dungeon's name in capitals.
/// </summary>
public static class MapScreen
{
    internal static readonly string[] promptWords = { "open", "party", "finder", "center", "player", "pan", "zoom", "pin", "location", "close" };

    /// <summary>
    /// <c>lines</c> are the window's text with positions as fractions of it. The bar is matched
    /// word by word with a majority, because the fast recogniser (Vision's fallback) reads
    /// it as "Centeron Playpr • Pini&amp;Kation O Close" — four words of ten is the map; the
    /// skill tree's bar has three at most.
    /// How many of the bar's words a reading holds.
    /// </summary>
    public static int promptWordCount(string bar)
    {
        string letters = new string(bar.ToLowerInvariant().Where(c => char.IsLetter(c)).ToArray());
        return promptWords.Count(w => letters.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>force</c>: the caller has another reason to believe this is the map (the red MAP tab)
    /// and wants the areas read regardless of the bar.
    /// </summary>
    public static (bool isMap, List<string> areas, string bar) read(IReadOnlyList<(string text, double x, double y)> lines, bool force = false)
    {
        string bar = string.Join(" ", lines.Where(l => l.y > 0.9).Select(l => l.text));
        if (!(force || promptWordCount(bar) >= 4)) return (false, new List<string>(), bar);
        // The panel: title-case lines in the left third, below the dungeon's name in
        // capitals (which follows "WORLD MAP" and its entry).
        List<string> areas = new List<string>();
        bool underDungeon = false;
        foreach (var line in lines.Where(l => l.x < 0.33 && l.y > 0.3 && l.y < 0.8))
        {
            string text = SwiftStr.trimWhitespaces(line.text);
            string word = SwiftStr.letters(text);
            if (word.Length < 4) continue;
            // A header is in capitals — mostly, with the fast recogniser ("NIGFrrMARE"). The
            // dungeon's own header opens the list; the next one (the season's quest box,
            // "CONQUER A NIGHTMARE DUNGEON") closes it.
            if (word.Count(c => char.IsUpper(c)) * 10 >= word.Length * 7)
            {
                if (underDungeon) break;
                underDungeon = !text.ToUpperInvariant().Contains("WORLD MAP", StringComparison.Ordinal);
                continue;
            }
            if (underDungeon) areas.Add(Objective.clean(SwiftStr.dropLeadingNonLetters(text)));
        }
        return (true, areas, bar);
    }

    /// <summary>Whether a panel line reads like a place name and not the fast recogniser's rubbish.</summary>
    public static bool looksLikeName(string text)
    {
        string letters = SwiftStr.letters(text);
        return letters.Length >= 4 && text.Length > 0 && char.IsUpper(text[0])
            && letters.Count(c => char.IsUpper(c)) * 2 < letters.Length
            && text.All(c => char.IsLetter(c) || c == ' ' || c == '\'' || c == '’' || c == '-');
    }
}

/// <summary>
/// The Swift string operations this file leans on, spelled out so that each one's character
/// set is explicit. File-local: other ported files carry their own.
/// </summary>
file static class SwiftStr
{
    /// <summary>Foundation's <c>.whitespaces</c>: Unicode general category Zs plus tab (U+0009).
    /// (C#'s bare Trim() would also take newlines and other separators.)</summary>
    public static bool isWhitespace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary><c>trimmingCharacters(in: .whitespaces)</c>.</summary>
    public static string trimWhitespaces(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && isWhitespace(s[start])) start++;
        while (end > start && isWhitespace(s[end - 1])) end--;
        return s.Substring(start, end - start);
    }

    /// <summary>Membership of Foundation's <c>CharacterSet.letters</c>: categories L* and M*
    /// (letters and combining marks). PORT NOTE: wider than <c>Character.isLetter</c> (L* only),
    /// as in Swift, where the two are also different sets.</summary>
    public static bool inLetterSet(char c)
    {
        if (char.IsLetter(c)) return true;
        UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark;
    }

    /// <summary><c>trimmingCharacters(in: CharacterSet.letters.inverted)</c>: trims everything
    /// that is not a letter or mark from both ends.</summary>
    public static string trimNonLetters(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && !inLetterSet(s[start])) start++;
        while (end > start && !inLetterSet(s[end - 1])) end--;
        return s.Substring(start, end - start);
    }

    /// <summary><c>drop { !$0.isLetter }</c>: the string from its first letter.</summary>
    public static string dropLeadingNonLetters(string s)
    {
        int start = 0;
        while (start < s.Length && !char.IsLetter(s[start])) start++;
        return s.Substring(start);
    }

    /// <summary><c>filter(\.isLetter)</c>.</summary>
    public static string letters(string s) => new string(s.Where(c => char.IsLetter(c)).ToArray());

    /// <summary><c>filter(\.isNumber)</c> — see the header PORT NOTE: <c>char.IsDigit</c> here.</summary>
    public static string digits(string s) => new string(s.Where(c => char.IsDigit(c)).ToArray());

    /// <summary>Swift's <c>Int(String)</c>: an optional leading sign and ASCII digits, nothing
    /// else — no whitespace, no empty string, nil on overflow.</summary>
    public static int? toInt(string s) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : null;
}
