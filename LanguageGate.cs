using System.Reflection;

namespace Feather;

/// <summary>Arabic-only input policy: any letter outside the Arabic script counts as a foreign language.</summary>
internal static class ArabicText
{
    internal static bool IsArabicLetter(char letter) => letter switch
    {
        >= '\u0600' and <= '\u06FF' => true, // Arabic
        >= '\u0750' and <= '\u077F' => true, // Arabic Supplement
        >= '\u0870' and <= '\u089F' => true, // Arabic Extended-B
        >= '\u08A0' and <= '\u08FF' => true, // Arabic Extended-A
        >= '\uFB50' and <= '\uFDFF' => true, // Arabic Presentation Forms-A
        >= '\uFE70' and <= '\uFEFF' => true, // Arabic Presentation Forms-B
        _ => false
    };

    /// <summary>True when the text contains a letter of Latin, Cyrillic, Greek or any other non-Arabic script.
    /// Digits, punctuation and whitespace stay allowed so addresses and numbers remain usable.</summary>
    internal static bool ContainsForeignLetter(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var letter in text)
            if (char.IsLetter(letter) && !IsArabicLetter(letter))
                return true;
        return false;
    }
}

/// <summary>Window shown as soon as a foreign script is typed; carries the warning picture.</summary>
internal sealed class LanguageGateForm : Form
{
    private readonly Image? _picture;

    internal LanguageGateForm()
    {
        Text = "اللغة العربية فقط";
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 10);
        KeyPreview = true;
        ClientSize = new Size(340, 338);

        _picture = LoadPicture();
        var image = new PictureBox
        {
            Image = _picture,
            Location = new Point(42, 14),
            Size = new Size(256, 144),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = _picture is null ? Palette.Raised : Color.Black,
            Enabled = false
        };
        Controls.Add(image);

        Controls.Add(new Label
        {
            Text = "اللغة العربية فقط",
            Location = new Point(14, 166),
            Size = new Size(312, 28),
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = Palette.Accent,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            RightToLeft = RightToLeft.Yes
        });

        Controls.Add(new Label
        {
            Text = "لا يقبل هذا المتصفح أي لغة غير العربية.\nامسح النص المكتوب بحروف أجنبية واكتب بالعربية.",
            Location = new Point(14, 200),
            Size = new Size(312, 74),
            Font = new Font("Segoe UI", 10),
            ForeColor = Palette.Text,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            RightToLeft = RightToLeft.Yes
        });

        var ok = new Button
        {
            Text = "حسنًا",
            Location = new Point(216, 286),
            Size = new Size(110, 38),
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.Raised,
            ForeColor = Palette.Accent,
            Font = new Font("Segoe UI", 11),
            Cursor = Cursors.Hand,
            RightToLeft = RightToLeft.Yes,
            UseVisualStyleBackColor = false
        };
        ok.FlatAppearance.BorderSize = 1;
        ok.FlatAppearance.BorderColor = Palette.Accent;
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
    }

    private static Image? LoadPicture()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(resource => resource.EndsWith("gate.png", StringComparison.OrdinalIgnoreCase));
            if (name is null || assembly.GetManifestResourceStream(name) is not { } stream)
                return null;
            using (stream)
            using (var decoded = Image.FromStream(stream))
                return new Bitmap(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _picture?.Dispose();
        base.Dispose(disposing);
    }
}
