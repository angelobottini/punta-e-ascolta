using PuntaEAscolta.Core;
using PuntaEAscolta.Core.Abstractions;
using PuntaEAscolta.Core.Reading;
using PuntaEAscolta.Core.Settings;
using PuntaEAscolta.Logic.Text;

namespace PuntaEAscolta.Logic.Resolution;

/// <summary>Testo scelto tra le righe OCR. Text è già ripulito (scorciatoie e simboli spuri tolti).</summary>
public sealed record OcrSelection(ReadSource Source, string Text, int LineCount);

/// <summary>
/// Sceglie, tra le righe OCR di una zona, che cosa leggere in base alla posizione del puntatore.
/// Regole misurate in docs/research/probe-affinity.md: una riga OCR può fondere elementi affiancati (etichetta e
/// scorciatoia, schede, etichetta e casella) quindi viene spezzata in segmenti dove lo spazio fra parole supera 0,6 volte
/// l'altezza di riga; candidata è la riga la cui fascia verticale, allargata di 0,35 altezze, contiene il puntatore;
/// se il segmento puntato è una scorciatoia si prende l'etichetta più vicina a sinistra.
/// </summary>
public static class PointerTextSelector
{
    private const double SegmentGapInLineHeights = 0.6;
    private const double RowPaddingInLineHeights = 0.35;
    private const double HorizontalToleranceInLineHeights = 0.5;
    private const double MaxLabelSearchInLineHeights = 20;

    /// <param name="pointerX">Coordinata X del puntatore nell'immagine passata all'OCR.</param>
    /// <param name="pointerY">Coordinata Y del puntatore nell'immagine passata all'OCR.</param>
    /// <param name="wholeZone">True per leggere tutte le righe della zona, ordinate (pressione prolungata).</param>
    /// <param name="clipTo">Se presente, considera solo le righe che intersecano questo rettangolo (es. rettangolo dell'elemento trovato dall'accessibilità).</param>
    public static OcrSelection? Select(OcrResult result, double pointerX, double pointerY, OcrSettings settings, bool wholeZone, ImageRect? clipTo = null)
    {
        if (result is null || result.Lines.Count == 0) return null;
        var reading = new ReadingSettings();

        var segments = new List<Segment>();
        foreach (var line in result.Lines)
        {
            if (line.Box.Height <= 0 || line.Box.Width <= 0) continue;
            if (clipTo is { } clip && !Intersects(line.Box, clip)) continue;
            segments.AddRange(SplitIntoSegments(line));
        }
        if (segments.Count == 0) return null;

        if (wholeZone)
        {
            if (!settings.GroupLinesIntoBlocks)
            {
                var ordered = segments.OrderBy(s => s.Box.Y + s.Box.Height / 2).ThenBy(s => s.Box.X).ToList();
                var texts = MergeRows(ordered).Select(t => LabelCleaner.CleanOcrLine(t, reading)).Where(t => t.Length > 0).ToList();
                if (texts.Count == 0) return null;
                return new OcrSelection(ReadSource.OcrZone, string.Join(". ", texts), texts.Count);
            }
            return SelectWholeZoneByBlocks(segments, pointerX, pointerY, settings, reading);
        }

        // Riga candidata: fascia verticale allargata che contiene il puntatore; altrimenti la più vicina entro il limite.
        var onRow = segments.Where(s => VerticalDistance(s.Box, pointerY) <= RowPaddingInLineHeights * s.Box.Height).ToList();
        if (onRow.Count == 0)
        {
            var nearest = segments.MinBy(s => VerticalDistance(s.Box, pointerY) / Math.Max(1, s.Box.Height));
            if (nearest is null) return null;
            double lineDistance = VerticalDistance(nearest.Box, pointerY) / Math.Max(1, nearest.Box.Height);
            if (lineDistance > settings.MaxLineDistanceInLineHeights) return null;
            double rowY = nearest.Box.Y + nearest.Box.Height / 2;
            onRow = segments.Where(s => Math.Abs(s.Box.Y + s.Box.Height / 2 - rowY) <= RowPaddingInLineHeights * s.Box.Height).ToList();
        }

        // Un carattere isolato (icona o freccia letta come lettera) non è un candidato se sulla riga c'è altro.
        var meaningful = onRow.Where(s => !s.IsNoise).ToList();
        if (meaningful.Count > 0) onRow = meaningful;

        double h = onRow.Average(s => s.Box.Height);
        double tolerance = HorizontalToleranceInLineHeights * h;

        // Segmento che contiene la X del puntatore (con tolleranza), altrimenti il più vicino orizzontalmente sulla stessa riga.
        var hit = onRow.Where(s => pointerX >= s.Box.X - tolerance && pointerX <= s.Box.Right + tolerance)
                       .OrderBy(s => HorizontalDistance(s.Box, pointerX)).FirstOrDefault();
        Segment? chosen = hit;
        if (chosen is null)
        {
            double limit = (clipTo is null ? MaxLabelSearchInLineHeights : 60) * h;
            // Preferenza per l'etichetta a sinistra del puntatore (il clic nella parte vuota di una riga di menu)
            var left = onRow.Where(s => s.Box.Right <= pointerX && pointerX - s.Box.Right <= limit && !s.IsShortcut).OrderByDescending(s => s.Box.Right).FirstOrDefault();
            var right = onRow.Where(s => s.Box.X >= pointerX && s.Box.X - pointerX <= 3 * h && !s.IsShortcut).OrderBy(s => s.Box.X).FirstOrDefault();
            chosen = left ?? right;
        }
        else if (chosen.IsShortcut)
        {
            chosen = onRow.Where(s => !s.IsShortcut && s.Box.X < chosen.Box.X).OrderByDescending(s => s.Box.Right).FirstOrDefault();
        }
        if (chosen is null) return null;

        // Raggruppamento in blocco: righe adiacenti con altezza simile, sovrapposizione orizzontale e piccolo spazio verticale.
        if (settings.GroupLinesIntoBlocks)
        {
            var block = BuildBlock(segments, chosen, settings.BlockMaxGapInLineHeights);
            if (block.Count > 1)
            {
                var texts = block.OrderBy(s => s.Box.Y + s.Box.Height / 2).ThenBy(s => s.Box.X)
                                 .Select(s => LabelCleaner.CleanOcrLine(s.Text, reading)).Where(t => t.Length > 0).ToList();
                if (texts.Count > 1) return new OcrSelection(ReadSource.OcrBlock, JoinBlock(texts), texts.Count);
                if (texts.Count == 1) return new OcrSelection(ReadSource.OcrLine, texts[0], 1);
            }
        }

        var text = LabelCleaner.CleanOcrLine(chosen.Text, reading);
        return text.Length == 0 ? null : new OcrSelection(ReadSource.OcrLine, text, 1);
    }

    /// <summary>
    /// Lettura dell'intera zona per blocchi (colonne, cartelli, paragrafi): prima il blocco sotto il puntatore (o il più vicino),
    /// poi gli altri in ordine di lettura (dall'alto, poi da sinistra). Ordinare solo per Y mescolava le righe di colonne diverse
    /// ("ATTENZIONE: [riga del browser] È VIETATO [riga del browser] L'ACCESSO").
    /// </summary>
    private static OcrSelection? SelectWholeZoneByBlocks(List<Segment> segments, double pointerX, double pointerY, OcrSettings settings, ReadingSettings reading)
    {
        var blocks = new List<List<Segment>>();
        var remaining = segments.OrderBy(s => s.Box.Y).ThenBy(s => s.Box.X).ToList();
        while (remaining.Count > 0)
        {
            var block = BuildBlock(remaining, remaining[0], settings.BlockMaxGapInLineHeights);
            foreach (var s in block) remaining.Remove(s);
            blocks.Add(block);
        }

        double lineHeight = Math.Max(1, segments.Average(s => s.Box.Height));
        var first = blocks.MinBy(b => DistanceTo(Bounds(b), pointerX, pointerY))!;
        var order = new List<List<Segment>> { first };
        order.AddRange(blocks.Where(b => !ReferenceEquals(b, first))
                             .OrderBy(b => Math.Round(Bounds(b).Y / lineHeight))
                             .ThenBy(b => Bounds(b).X));

        var parts = new List<string>();
        int lineCount = 0;
        foreach (var block in order)
        {
            var rows = MergeRows(block.OrderBy(s => s.Box.Y + s.Box.Height / 2).ThenBy(s => s.Box.X).ToList())
                .Select(t => LabelCleaner.CleanOcrLine(t, reading)).Where(t => t.Length > 0).ToList();
            if (rows.Count == 0) continue;
            lineCount += rows.Count;
            parts.Add(JoinRows(rows));
        }
        if (parts.Count == 0) return null;
        return new OcrSelection(ReadSource.OcrZone, JoinRows(parts, forceBreak: true), lineCount);
    }

    /// <summary>
    /// Unisce righe o blocchi da pronunciare: trattino di sillabazione ricongiunto; spazio se la riga finisce già con la
    /// punteggiatura o se la successiva continua la frase (minuscola); altrimenti ". " per dare la pausa fra righe distinte.
    /// </summary>
    private static string JoinRows(List<string> rows, bool forceBreak = false)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in rows)
        {
            if (sb.Length > 0)
            {
                char last = sb[^1];
                bool nextLower = t.Length > 0 && char.IsLower(t[0]);
                if (!forceBreak && last == '-' && nextLower) sb.Length--;
                else if (last is '.' or '!' or '?' or ':' or ';' or ',' or '…') sb.Append(' ');
                else if (!forceBreak && nextLower) sb.Append(' ');
                else sb.Append(". ");
            }
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static ImageRect Bounds(List<Segment> block)
    {
        double x = block.Min(s => s.Box.X), y = block.Min(s => s.Box.Y);
        double r = block.Max(s => s.Box.Right), b = block.Max(s => s.Box.Bottom);
        return new ImageRect(x, y, r - x, b - y);
    }

    private static double DistanceTo(ImageRect box, double x, double y)
    {
        double dx = HorizontalDistance(box, x), dy = VerticalDistance(box, y);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private sealed record Segment(string Text, ImageRect Box, bool IsShortcut)
    {
        /// <summary>Segmento di un solo carattere non numerico: quasi sempre un'icona, una spunta o una freccia scambiata per lettera.</summary>
        public bool IsNoise => Text.Length == 1 && !char.IsDigit(Text[0]);
    }

    /// <summary>Spezza una riga OCR nei suoi segmenti separati da spazi larghi; senza parole con riquadro usa il testo intero.</summary>
    private static IEnumerable<Segment> SplitIntoSegments(OcrLine line)
    {
        var words = line.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Box.Width > 0).OrderBy(w => w.Box.X).ToList();
        if (words.Count == 0)
        {
            yield return new Segment(line.Text, line.Box, LabelCleaner.IsKeyboardShortcut(line.Text));
            yield break;
        }
        double h = Math.Max(1, line.Box.Height);
        var current = new List<OcrWord> { words[0] };
        for (int i = 1; i < words.Count; i++)
        {
            double gap = words[i].Box.X - current[^1].Box.Right;
            if (gap > SegmentGapInLineHeights * h)
            {
                yield return Make(current);
                current = new List<OcrWord>();
            }
            current.Add(words[i]);
        }
        yield return Make(current);

        static Segment Make(List<OcrWord> ws)
        {
            var text = string.Join(' ', ws.Select(w => w.Text.Trim())).Trim();
            double x = ws.Min(w => w.Box.X), y = ws.Min(w => w.Box.Y);
            double r = ws.Max(w => w.Box.Right), b = ws.Max(w => w.Box.Bottom);
            return new Segment(text, new ImageRect(x, y, r - x, b - y), LabelCleaner.IsKeyboardShortcut(text));
        }
    }

    private static List<Segment> BuildBlock(List<Segment> all, Segment seed, double maxGapInLineHeights)
    {
        var block = new List<Segment> { seed };
        var remaining = all.Where(s => !ReferenceEquals(s, seed) && !s.IsShortcut).ToList();
        bool grown = true;
        while (grown)
        {
            grown = false;
            foreach (var s in remaining.ToList())
            {
                if (block.Any(b => Adjacent(b, s, maxGapInLineHeights)))
                {
                    block.Add(s);
                    remaining.Remove(s);
                    grown = true;
                }
            }
        }
        return block;
    }

    private static bool Adjacent(Segment a, Segment b, double maxGapInLineHeights)
    {
        double ratio = a.Box.Height / Math.Max(1, b.Box.Height);
        if (ratio < 0.7 || ratio > 1.4) return false;
        double h = (a.Box.Height + b.Box.Height) / 2;
        double overlap = Math.Min(a.Box.Right, b.Box.Right) - Math.Max(a.Box.X, b.Box.X);
        if (overlap < Math.Min(a.Box.Width, b.Box.Width) * 0.3 && overlap < h) return false;
        double gap = a.Box.Y < b.Box.Y ? b.Box.Y - a.Box.Bottom : a.Box.Y - b.Box.Bottom;
        return gap <= maxGapInLineHeights * h && gap > -h * 0.5;
    }

    /// <summary>Unisce segmenti sulla stessa riga (stessa fascia verticale) con uno spazio, per la lettura dell'intera zona.</summary>
    private static IEnumerable<string> MergeRows(List<Segment> ordered)
    {
        var row = new List<Segment>();
        foreach (var s in ordered)
        {
            if (row.Count > 0)
            {
                var last = row[^1];
                double cy = s.Box.Y + s.Box.Height / 2, lcy = last.Box.Y + last.Box.Height / 2;
                if (Math.Abs(cy - lcy) > RowPaddingInLineHeights * Math.Max(s.Box.Height, last.Box.Height))
                {
                    yield return string.Join(' ', row.Where(r => !r.IsShortcut).Select(r => r.Text));
                    row.Clear();
                }
            }
            row.Add(s);
        }
        if (row.Count > 0) yield return string.Join(' ', row.Where(r => !r.IsShortcut).Select(r => r.Text));
    }

    /// <summary>Le righe di un blocco (paragrafo a capo, cartello) si uniscono con uno spazio: la punteggiatura presente dà già le pause; un trattino di sillabazione a fine riga viene ricongiunto.</summary>
    private static string JoinBlock(List<string> texts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in texts)
        {
            if (sb.Length > 0)
            {
                if (sb[^1] == '-' && t.Length > 0 && char.IsLower(t[0])) sb.Length--; // "informa-" + "zione"
                else sb.Append(' ');
            }
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static double VerticalDistance(ImageRect box, double y) => y < box.Y ? box.Y - y : y > box.Bottom ? y - box.Bottom : 0;
    private static double HorizontalDistance(ImageRect box, double x) => x < box.X ? box.X - x : x > box.Right ? x - box.Right : 0;
    private static bool Intersects(ImageRect a, ImageRect b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
}
