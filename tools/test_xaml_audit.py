#!/usr/bin/env python3
"""Regression tests for tools/xaml_audit.py (Phase 7 / Stage 3).

Each test rebuilds a minimal fixture reproducing a defect class that actually
shipped, and asserts the audit CATCHES it (and that the corrected form PASSES):

  * v0.7.18 field crash — LoginView referenced the removed 'NavItem' style key
    (renamed to NavRailItem): XamlParseException at startup, behind the modal
    error dialog that defeated the liveness-only smoke test.
  * v0.7.19 "fix" (itself defective) — InputField retargeted to TextBoxBase on
    the false claim that PasswordBox derives from TextBoxBase. It does NOT
    (PasswordBox : Control), so the crash survived the fix. The audit's WPF
    ancestry model must flag TextBoxBase-on-PasswordBox and accept
    Control-on-PasswordBox.
  * plus: BasedOn (missing target / incompatible TargetType), duplicate keys
    within a dictionary, missing merged-dictionary sources, missing converter
    classes, a clean fixture, the quoted-'>' tag-scanning false-negative class
    and the Dark/Light theme-parity guard-rail (Phase 7.1 review).

Run:  python3 -m unittest discover -s tools -p "test_xaml_audit*.py" -v
Stdlib only.
"""
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import xaml_audit  # noqa: E402

XNS = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"'
XNSX = XNS + ' xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'

APP_XAML = (
    '<Application ' + XNSX + '>\n'
    '  <Application.Resources>\n'
    '    <ResourceDictionary>\n'
    '      <ResourceDictionary.MergedDictionaries>\n'
    '        <ResourceDictionary Source="Themes/{theme}.xaml" />\n'
    '      </ResourceDictionary.MergedDictionaries>\n'
    '    </ResourceDictionary>\n'
    '  </Application.Resources>\n'
    '</Application>\n'
)

COMPONENTS = (
    '<ResourceDictionary ' + XNSX + '>\n'
    '  <Style x:Key="NavRailItem" TargetType="Border"/>\n'
    '  {input_style}\n'
    '</ResourceDictionary>\n'
)

INPUT_TB = '<Style x:Key="InputField" TargetType="TextBox"/>'
INPUT_TBB = '<Style x:Key="InputField" TargetType="{x:Type TextBoxBase}"/>'
INPUT_CTRL = '<Style x:Key="InputField" TargetType="{x:Type Control}"/>'

WINDOW_TEMPLATE = (
    '<Window ' + XNSX + '\n'
    '        Title="MainWindow" Height="450" Width="800">\n'
    '  {body}\n'
    '</Window>\n'
)

RD = '<ResourceDictionary ' + XNSX + '>{body}</ResourceDictionary>'


class XamlAuditTest(unittest.TestCase):
    def setUp(self):
        self._tmp = Path(tempfile.mkdtemp(prefix="xaml_audit_test_"))

    def tearDown(self):
        shutil.rmtree(self._tmp, ignore_errors=True)

    def build(self, files, corpus=None):
        for rel, content in files.items():
            p = self._tmp / "app" / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(content, encoding="utf-8")
        corpus_dir = self._tmp / "src"
        corpus_dir.mkdir(exist_ok=True)
        for rel, content in (corpus or {}).items():
            p = corpus_dir / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(content, encoding="utf-8")
        return xaml_audit.audit(self._tmp / "app", corpus_dirs=[corpus_dir])

    def errors(self, findings):
        return [f for f in findings if f.severity == "ERROR"]

    def codes(self, findings):
        return {f.code for f in findings}

    # ── v0.7.18: missing NavItem style key (renamed to NavRailItem) ──
    def test_v0718_missing_navitem_key_flagged(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=""),
            "Views/MainWindow.xaml": WINDOW_TEMPLATE.format(
                body='<Border Style="{StaticResource NavItem}"/>'),
        })
        codes = self.codes(self.errors(findings))
        self.assertIn("XAML_MISSING_KEY", codes, f"expected missing-key error, got: {findings}")
        self.assertTrue(any("NavItem" in f.message for f in findings))

    def test_renamed_key_resolves(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=""),
            "Views/MainWindow.xaml": WINDOW_TEMPLATE.format(
                body='<Border Style="{StaticResource NavRailItem}"/>'),
        })
        self.assertEqual(self.errors(findings), [])

    # ── v0.7.19: InputField TargetType=TextBoxBase applied to PasswordBox ──
    def test_v0719_textboxbase_style_on_passwordbox_flagged(self):
        # The v0.7.19 commit retargeted InputField to TextBoxBase on the false
        # claim that PasswordBox derives from TextBoxBase. It derives from
        # Control. The audit's ancestry model must catch this — it is the
        # defect that shipped in HEAD until the Phase 7 audit re-ran.
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_TBB),
            "Views/LoginView.xaml": WINDOW_TEMPLATE.format(
                body='<PasswordBox Style="{StaticResource InputField}"/>'),
        })
        codes = self.codes(self.errors(findings))
        self.assertIn("XAML_TARGETTYPE_MISMATCH", codes,
                      f"TextBoxBase-targeted style on PasswordBox must be flagged, got: {findings}")

    def test_textboxbase_style_on_textbox_ok(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_TBB),
            "Views/LoginView.xaml": WINDOW_TEMPLATE.format(
                body='<TextBox Style="{StaticResource InputField}"/>'),
        })
        self.assertEqual(self.errors(findings), [])

    def test_control_style_on_passwordbox_ok(self):
        # The actual Phase 7 fix: TargetType=Control is the real common
        # ancestor — must NOT be flagged.
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_CTRL),
            "Views/LoginView.xaml": WINDOW_TEMPLATE.format(
                body='<PasswordBox Style="{StaticResource InputField}"/>'
                     '<TextBox Style="{StaticResource InputField}"/>'),
        })
        self.assertEqual(self.errors(findings), [])

    def test_control_style_on_border_flagged(self):
        # Border is NOT a Control — a Control-targeted style must still fail.
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_CTRL),
            "Views/LoginView.xaml": WINDOW_TEMPLATE.format(
                body='<Border Style="{StaticResource InputField}"/>'),
        })
        self.assertIn("XAML_TARGETTYPE_MISMATCH", self.codes(self.errors(findings)))

    def test_quoted_gt_in_attribute_still_flagged(self):
        # Phase 7.1 review fix M5: a '>' INSIDE an attribute value must not
        # make the tag-scanning regex stop early and skip the Style check
        # (false-negative class found by the adversarial review).
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_TBB),
            "Views/LoginView.xaml": WINDOW_TEMPLATE.format(
                body='<PasswordBox Content=">" Style="{StaticResource InputField}"/>'),
        })
        self.assertIn("XAML_TARGETTYPE_MISMATCH", self.codes(self.errors(findings)))

    def test_quoted_gt_does_not_break_clean_fixture(self):
        # The sentinel substitution must not create phantom findings.
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_CTRL),
            "Views/LoginView.xaml": WINDOW_TEMPLATE.format(
                body='<PasswordBox Content=">" Style="{StaticResource InputField}"/>'),
        })
        self.assertEqual(self.errors(findings), [])

    # ── BasedOn chains ──
    def test_basedon_missing_target_flagged(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(
                input_style='<Style x:Key="FieldLabel" TargetType="TextBlock" '
                            'BasedOn="{StaticResource BodyText}"/>'),
        })
        self.assertIn("XAML_BASEDON_MISSING", self.codes(self.errors(findings)))

    def test_basedon_incompatible_targettype_flagged(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": RD.format(
                body='<Style x:Key="BaseStyle" TargetType="TextBlock"/>'
                     '<Style x:Key="Derived" TargetType="Border" '
                     'BasedOn="{StaticResource BaseStyle}"/>'),
        })
        self.assertIn("XAML_BASEDON_INCOMPATIBLE", self.codes(self.errors(findings)))

    def test_basedon_compatible_targettype_ok(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": RD.format(
                body='<Style x:Key="BaseStyle" TargetType="TextBoxBase"/>'
                     '<Style x:Key="Derived" TargetType="TextBox" '
                     'BasedOn="{StaticResource BaseStyle}"/>'),
        })
        self.assertEqual(self.errors(findings), [])

    # ── duplicate keys ──
    def test_duplicate_key_within_file_flagged(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": RD.format(
                body='<SolidColorBrush x:Key="BrandBrush" Color="#FF0000"/>'
                     '<SolidColorBrush x:Key="BrandBrush" Color="#00FF00"/>'),
        })
        self.assertIn("XAML_DUP_KEY_IN_FILE", self.codes(self.errors(findings)))

    # ── merged dictionary integrity ──
    def test_missing_merged_source_flagged(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="DoesNotExist"),
        })
        self.assertIn("XAML_MERGED_SOURCE_MISSING", self.codes(self.errors(findings)))

    # ── converter / custom-class existence ──
    def test_missing_converter_class_flagged(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": (
                '<ResourceDictionary ' + XNS +
                ' xmlns:conv="clr-namespace:PrintShopCompanion.App.Converters"'
                ' xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">'
                '<conv:MissingConverter x:Key="Missing"/>'
                '</ResourceDictionary>'),
            "Views/MainWindow.xaml": WINDOW_TEMPLATE.format(body=""),
        }, corpus={"PrintShopCompanion/App/Converters/Converters.cs":
                   "namespace PrintShopCompanion.App.Converters;\n"
                   "public sealed class RealConverter {}\n"})
        self.assertIn("XAML_CLASS_MISSING", self.codes(self.errors(findings)))

    def test_existing_converter_class_ok(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": (
                '<ResourceDictionary ' + XNS +
                ' xmlns:conv="clr-namespace:PrintShopCompanion.App.Converters"'
                ' xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">'
                '<conv:RealConverter x:Key="Real"/>'
                '</ResourceDictionary>'),
        }, corpus={"PrintShopCompanion/App/Converters/Converters.cs":
                   "namespace PrintShopCompanion.App.Converters;\n"
                   "public sealed class RealConverter {}\n"})
        self.assertEqual(self.errors(findings), [])

    # ── theme parity guard-rail (Phase 7.1 review fix L2) ──
    def test_theme_key_divergence_warns(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_CTRL),
            "Themes/Light.xaml": RD.format(body='<SolidColorBrush x:Key="OnlyInLight" Color="#111111"/>'),
            "Themes/Dark.xaml": RD.format(body='<SolidColorBrush x:Key="OnlyInDark" Color="#222222"/>'),
        })
        self.assertIn("XAML_THEME_KEY_DIVERGENCE", {f.code for f in findings})

    def test_theme_key_parity_is_silent(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_CTRL),
            "Themes/Light.xaml": RD.format(body='<SolidColorBrush x:Key="Shared" Color="#111111"/>'),
            "Themes/Dark.xaml": RD.format(body='<SolidColorBrush x:Key="Shared" Color="#222222"/>'),
        })
        self.assertNotIn("XAML_THEME_KEY_DIVERGENCE", {f.code for f in findings})
        self.assertEqual(self.errors(findings), [])

    # ── clean fixture ──
    def test_clean_fixture_zero_errors(self):
        findings = self.build({
            "App.xaml": APP_XAML.format(theme="Components"),
            "Themes/Components.xaml": COMPONENTS.format(input_style=INPUT_CTRL),
            "Views/LoginView.xaml": WINDOW_TEMPLATE.format(
                body='<PasswordBox Style="{StaticResource InputField}"/>'),
        })
        self.assertEqual(self.errors(findings), [])


if __name__ == "__main__":
    unittest.main()
