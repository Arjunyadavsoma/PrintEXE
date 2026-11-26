namespace PrintShopCompanion.Infrastructure.Pdf;

using System.Text;

/// <summary>
/// Generates a minimal but <b>spec-correct</b> 1-page test PDF entirely in
/// managed code — no PDF library required (decision D-04).
///
/// <para>Replaces the previous hand-crafted test PDF whose <c>/Length</c>
/// (90) did not match the actual stream bytes and whose xref offsets were
/// hardcoded guesses. Strict PDF parsers (and PDFium's recovery mode) may
/// accept such files, but correctness is cheap to compute: every xref offset
/// below is measured while the file is being written.</para>
///
/// <para>Thread-safe. Platform-neutral (used by unit tests on any OS).</para>
/// </summary>
public static class TestPagePdf
{
    /// <summary>Writes a valid 1-page A4 test PDF to <paramref name="path"/> and returns the path.</summary>
    public static async Task<string> WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = BuildBytes("PrintEXE test print", "If you can read this page, the print pipeline works.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    /// <summary>Builds the PDF bytes. Internal so unit tests can assert structure (xref offsets, /Length).</summary>
    public static byte[] BuildBytes(string title, string subtitle)
    {
        var objects = new List<string>
        {
            // 1: catalog
            "<< /Type /Catalog /Pages 2 0 R >>",
            // 2: pages
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            // 3: page (A4 = 595 x 842 pt)
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            // 4: content stream (filled below — its /Length must match byte-exactly)
            string.Empty,
            // 5: font
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        // PDFDocEncoding of the two text lines (ASCII subset is safe for our own titles).
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var content =
            $"BT /F1 20 Tf 72 760 Td ({Esc(title)}) Tj ET\n" +
            $"BT /F1 12 Tf 72 720 Td ({Esc(subtitle)}) Tj ET\n";
        var contentBytes = Encoding.ASCII.GetBytes(content);
        objects[3] = $"<< /Length {contentBytes.Length} >>\nstream\n{content}endstream";

        // Serialize with header + objects, recording each object's byte offset.
        using var ms = new MemoryStream();
        void Write(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Write("%PDF-1.4\n");
        var offsets = new int[objects.Count + 1]; // 1-based object numbers
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = (int)ms.Position;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefStart = (int)ms.Position;
        var count = objects.Count + 1;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(count).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < count; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer << /Size ").Append(count).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefStart).Append("\n%%EOF\n");
        Write(sb.ToString());

        return ms.ToArray();
    }
}
