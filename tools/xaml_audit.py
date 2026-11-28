#!/usr/bin/env python3
"""Static XAML resource-graph audit for PrintShopCompanion.App (Phase 7 / Stage 3).

Catches, BEFORE any Windows runner is provisioned, the defect classes that
shipped broken releases:

  * unresolved StaticResource / DynamicResource keys        (v0.7.15 "NavItem")
  * Style TargetType vs element type mismatches            (v0.7.19 InputField
    TargetType=TextBox applied to a PasswordBox)
  * broken BasedOn chains (missing target, incompatible TargetType)
  * duplicate resource keys (within one dictionary, and across the merged
    dictionaries where a later dictionary silently overrides an earlier one)
  * converter / custom-class existence (every xmlns clr-namespace element must
    resolve to a class that exists in the source corpus)
  * merged-dictionary integrity (missing Source files, self-inclusion cycles,
    XML well-formedness of every dictionary)

Usage:
    python3 tools/xaml_audit.py src/PrintShopCompanion.App [--report FILE]

Exit codes: 0 = no ERROR-severity findings; 1 = at least one ERROR; 2 = usage.
Stdlib only — runs on any CI image with Python 3.9+.

Known limitations (Phase 7.1 review):
  * Views' own <Window.Resources><ResourceDictionary.MergedDictionaries> are
    not modeled (none exist today; a future one would need visible_for /
    resolve_style extended — until then a View-merged key yields a spurious
    XAML_MISSING_KEY, i.e. fail-closed).
  * ThemeService swaps Dark.xaml for Light.xaml at RUNTIME (pack URI). The
    audit models the default (Light) state; a Dark/Light key-set divergence
    is reported as a WARNING (XAML_THEME_KEY_DIVERGENCE) guard-rail.
"""
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

# WPF type ancestry (subset) — enough to decide TextBox-vs-PasswordBox style
# mismatches and the common control family. Both names must be known for a
# mismatch to be an ERROR; unknown types (custom controls) yield a WARNING.
# NOTE (Phase 7 review): PasswordBox derives from Control, NOT TextBoxBase —
# the v0.7.19 commit message asserted otherwise and shipped a still-broken fix.
_ANCESTRY = {
    "TextBlock": "FrameworkElement",
    "Control": "FrameworkElement",
    "ContentControl": "Control",
    "Label": "ContentControl",
    "Button": "ButtonBase", "ButtonBase": "ContentControl", "RepeatButton": "ButtonBase",
    "ToggleButton": "ButtonBase", "CheckBox": "ToggleButton", "RadioButton": "ToggleButton",
    "TextBoxBase": "Control", "TextBox": "TextBoxBase", "RichTextBox": "TextBoxBase",
    "PasswordBox": "Control",  # deliberately NOT a TextBoxBase — the v0.7.19 defect
    "ComboBox": "ItemsControl", "ComboBoxItem": "ContentControl",
    "ListBox": "Selector", "Selector": "ItemsControl", "ItemsControl": "Control",
    "ListView": "View", "TreeView": "ItemsControl", "Menu": "ItemsControl",
    "TabControl": "Selector", "TabItem": "HeaderedContentControl",
    "HeaderedContentControl": "ContentControl", "UserControl": "ContentControl",
    "Window": "ContentControl", "Page": "FrameworkElement",
    "Panel": "FrameworkElement", "Grid": "Panel", "StackPanel": "Panel",
    "WrapPanel": "Panel", "DockPanel": "Panel", "Canvas": "Panel",
    "Border": "FrameworkElement", "Image": "FrameworkElement",
    "Separator": "Control", "ProgressBar": "RangeBase", "RangeBase": "Control",
    "Slider": "RangeBase", "ScrollBar": "RangeBase", "Thumb": "FrameworkElement",
    "Expander": "HeaderedContentControl", "GroupBox": "HeaderedContentControl",
    "ToolTip": "ContentControl", "MenuItem": "HeaderedItemsControl",
    "HeaderedItemsControl": "ItemsControl", "ScrollViewer": "ContentControl",
    "Viewbox": "FrameworkElement", "Hyperlink": None, "Run": None,
    "ItemsPresenter": "FrameworkElement", "ContentPresenter": "FrameworkElement",
    "GridSplitter": "Thumb",
}


@dataclass
class Finding:
    severity: str  # "ERROR" | "WARNING" | "INFO"
    code: str      # stable machine-readable id, e.g. XAML_MISSING_KEY
    file: str
    line: int
    message: str

    def format(self) -> str:
        return f"[{self.severity}] {self.code} {self.file}:{self.line} — {self.message}"


@dataclass
class StyleDef:
    key: str | None
    target_type: str | None
    based_on: str | None
    file: str
    line: int


def _line_of(text: str, pos: int) -> int:
    return text.count("\n", 0, pos) + 1


def _short(name: str) -> str:
    return name.split(".")[-1]


def derives(child: str, parent: str) -> bool:
    """True if `child` equals or inherits from `parent` per the WPF table."""
    c, p = _short(child), _short(parent)
    seen = set()
    while c and c not in seen:
        if c == p:
            return True
        seen.add(c)
        c = _ANCESTRY.get(c, "FrameworkElement" if c not in ("object",) else None)
    return False


def _load_corpus(src_root: Path) -> set[str]:
    """All `namespace X { class Y` pairs (C#) under src_root — converter /
    custom-class existence check."""
    classes: set[str] = set()
    for cs in src_root.rglob("*.cs"):
        if "obj" in cs.parts or "bin" in cs.parts:
            continue
        text = cs.read_text(encoding="utf-8", errors="replace")
        cur_ns = None
        for line in text.splitlines():
            m = re.match(r"\s*namespace\s+([\w.]+)", line)
            if m:
                cur_ns = m.group(1)
            m = re.search(r"\bclass\s+(\w+)", line)
            if m and cur_ns and not line.strip().startswith("//"):
                classes.add(f"{cur_ns}.{m.group(1)}")
                classes.add(m.group(1))
    return classes


_QUOTED_ATTR = re.compile(r'"[^"]*"')


def _protect_quoted_gt(text: str) -> str:
    """Replace '>' inside double-quoted attribute values with a 1:1 sentinel.

    Phase 7.1 review fix M5: tag-scanning regexes use [^>]*? and would stop at
    a '>' INSIDE an attribute value (e.g. Content=">"), silently skipping the
    tag and producing a false NEGATIVE in the TargetType/BasedOn checks. The
    sentinel is the same length, so byte offsets (and thus line numbers)
    remain valid."""
    return _QUOTED_ATTR.sub(lambda m: m.group(0).replace(">", "\x00"), text)


def _style_defs(text: str, rel: str) -> list[StyleDef]:
    """Parse <Style ...> opening tags (both x:Key/TargetType orders)."""
    out: list[StyleDef] = []
    for m in re.finditer(r"<Style\b([^>]*?)/?>", text, re.S):
        tag = m.group(1)
        key_m = re.search(r'x:Key="([^"]+)"', tag)
        tt_m = re.search(r'TargetType="(?:\{x:Type\s+)?([\w:.]+)\}?"', tag)
        bo_m = re.search(r'BasedOn="\{StaticResource\s+([^}]+?)\}"', tag)
        out.append(StyleDef(
            key=key_m.group(1) if key_m else None,
            target_type=tt_m.group(1) if tt_m else None,
            based_on=bo_m.group(1).strip() if bo_m else None,
            file=rel, line=_line_of(text, m.start()),
        ))
    return out


def audit(app_dir: Path, corpus_dirs: list[Path] | None = None) -> list[Finding]:
    """Run every check; return findings. ERROR findings fail CI."""
    findings: list[Finding] = []
    app_dir = app_dir.resolve()
    xamls = sorted(app_dir.rglob("*.xaml"))
    corpus_dirs = corpus_dirs or [app_dir.parent]  # src/ — the whole corpus
    corpus = set()
    for cdir in corpus_dirs:
        corpus |= _load_corpus(Path(cdir))

    texts: dict[Path, str] = {}
    # ── 1. XML well-formedness (merged-dictionary integrity baseline) ──
    for p in xamls:
        try:
            ET.parse(p)
            texts[p] = p.read_text(encoding="utf-8", errors="replace")
        except ET.ParseError as e:
            findings.append(Finding("ERROR", "XAML_MALFORMED_XML",
                                    str(p.relative_to(app_dir)), getattr(e, "position", (0, 0))[1],
                                    f"XML is not well-formed: {e}"))
            texts[p] = ""
    xamls = [p for p in xamls if texts.get(p)]
    # Quoted-'>'-protected copies for TAG-SCANNING checks (5 + 6). Byte
    # offsets are identical to the originals, so reported line numbers stay
    # valid. See _protect_quoted_gt (Phase 7.1 review fix M5).
    safe_texts = {p: _protect_quoted_gt(t) for p, t in texts.items()}

    # ── 2. App.xaml merged dictionaries: sources exist, no cycles ──
    app_xaml = app_dir / "App.xaml"
    merged: list[Path] = []
    if app_xaml.exists():
        app_text = texts.get(app_xaml, "")
        merged_root = app_dir / "App.xaml"
        md = re.search(r"<ResourceDictionary\.MergedDictionaries>(.*?)</ResourceDictionary\.MergedDictionaries>",
                       app_text, re.S)
        if md:
            for sm in re.finditer(r'Source="([^"]+)"', md.group(1)):
                src = sm.group(1)
                target = (app_xaml.parent / src).resolve()
                if not target.exists():
                    findings.append(Finding("ERROR", "XAML_MERGED_SOURCE_MISSING",
                                            "App.xaml", _line_of(app_text, sm.start()),
                                            f"merged dictionary Source does not exist: {src}"))
                elif target == merged_root:
                    findings.append(Finding("ERROR", "XAML_MERGED_CYCLE",
                                            "App.xaml", _line_of(app_text, sm.start()),
                                            "App.xaml includes itself"))
                else:
                    merged.append(target)

    # ── 3. Key tables — runtime-scoped. Keys visible app-wide come ONLY from
    # the ACTUALLY MERGED dictionaries (merge order: later wins) plus App.xaml's
    # own inline resources. Theme files that exist on disk but are NOT merged
    # (e.g. Fluent.xaml, a design reference) are NOT runtime-visible — styles
    # defined only there cannot satisfy a runtime reference.
    def defined_keys(p: Path) -> tuple[set[str], dict[str, StyleDef]]:
        text = texts.get(p, "")
        keys = set(re.findall(r'x:Key="([^"]+)"', text))
        styles = _style_defs(text, str(p.relative_to(app_dir)))
        for sd in styles:
            if sd.key is None and sd.target_type:
                keys.add(f"Type:{_short(sd.target_type)}")  # implicit style
        return keys, {sd.key: sd for sd in styles if sd.key}

    all_keys: dict[Path, set[str]] = {}
    all_styles: dict[Path, dict[str, StyleDef]] = {}
    for p in xamls:
        all_keys[p], all_styles[p] = defined_keys(p)

    app_keys: set[str] = set()
    for target in merged:
        app_keys |= all_keys.get(target, set())
    if app_xaml.exists():
        app_keys |= all_keys.get(app_xaml, set())

    # Runtime style table: merge ORDER matters (later merged dictionaries
    # override earlier ones). Per-file local resources win for their own file.
    merged_style_table: dict[str, StyleDef] = {}
    for target in merged:  # already in App.xaml merge order
        merged_style_table.update(all_styles.get(target, {}))

    def visible_for(p: Path) -> set[str]:
        return app_keys | all_keys.get(p, set())

    def resolve_style(p: Path, key: str) -> StyleDef | None:
        # Runtime resolution: the file's own Resources first, then the merged
        # dictionaries (last-merged wins).
        local = all_styles.get(p, {}).get(key)
        if local is not None:
            return local
        return merged_style_table.get(key)

    # ── 4. Resource references resolve ──
    ref_re = re.compile(r"\{(Static|Dynamic)Resource\s+([^}]+?)\s*\}")
    for p in xamls:
        rel = str(p.relative_to(app_dir))
        text = texts[p]
        for m in ref_re.finditer(text):
            kind, expr = m.group(1), m.group(2).strip()
            tm = re.match(r"\{x:Type\s+([\w:.]+)\}", expr)
            key = f"Type:{_short(tm.group(1))}" if tm else expr
            if not re.match(r"^[\w:]+$", expr) and not tm:
                continue  # non-key expression (e.g. {StaticResource {x:Null}}) — out of scope
            if key not in visible_for(p):
                sev = "ERROR" if kind == "Static" else "WARNING"
                code = "XAML_MISSING_KEY" if kind == "Static" else "XAML_MISSING_DYN_KEY"
                findings.append(Finding(sev, code, rel, _line_of(text, m.start()),
                                        f"unresolved {kind}Resource '{key}' — "
                                        + ("throws XamlParseException at startup" if kind == "Static"
                                           else "resolves to null at runtime (silent UI breakage)")))

    # ── 5. Style TargetType vs element compatibility (v0.7.19 class) ──
    elem_style_re = re.compile(r"<([\w:.]+)\b[^>]*?Style=\"\{StaticResource\s+([\w:]+)\}\"", re.S)
    for p in xamls:
        rel = str(p.relative_to(app_dir))
        text = safe_texts[p]
        for m in elem_style_re.finditer(text):
            elem, key = _short(m.group(1)), m.group(2)
            sd = resolve_style(p, key)
            if sd is None:
                continue  # missing keys already reported by check 4
            if sd.target_type is None:
                continue  # TargetType-less style (implicit) — cannot mismatch
            t_short = _short(sd.target_type)
            if t_short == elem or elem in ("Window", "UserControl", "Application"):
                continue
            if derives(elem, t_short):
                continue  # element is a legitimate subtype
            known = elem in _ANCESTRY and t_short in _ANCESTRY
            sev = "ERROR" if known else "WARNING"
            findings.append(Finding(sev, "XAML_TARGETTYPE_MISMATCH", rel, _line_of(text, m.start()),
                                    f"<{elem}> uses style '{key}' whose TargetType={sd.target_type} "
                                    f"(defined {sd.file}:{sd.line})"
                                    + ("" if known else " [unknown type pair — verify manually]")))

    # ── 6. BasedOn chains (missing target + TargetType compatibility) ──
    for p in xamls:
        rel = str(p.relative_to(app_dir))
        for sd in _style_defs(safe_texts[p], rel):
            if not sd.based_on:
                continue
            base_key = sd.based_on
            tm = re.match(r"\{x:Type\s+([\w:.]+)\}", base_key)
            lookup = f"Type:{_short(tm.group(1))}" if tm else base_key
            base = resolve_style(p, lookup)
            if base is None and lookup not in visible_for(p):
                findings.append(Finding("ERROR", "XAML_BASEDON_MISSING", rel, sd.line,
                                        f"BasedOn target '{lookup}' not found (style at {rel}:{sd.line})"))
                continue
            if base and base.target_type and sd.target_type:
                if not derives(sd.target_type, base.target_type):
                    findings.append(Finding("ERROR", "XAML_BASEDON_INCOMPATIBLE", rel, sd.line,
                                            f"style TargetType={sd.target_type} BasedOn '{base_key}' "
                                            f"whose TargetType={base.target_type} — not a subtype"))

    # ── 7. Duplicate resource keys ──
    for p in xamls:
        rel = str(p.relative_to(app_dir))
        text = texts[p]
        counts: dict[str, int] = {}
        for k in re.findall(r'x:Key="([^"]+)"', text):
            counts[k] = counts.get(k, 0) + 1
        for k, n in counts.items():
            if n > 1:
                findings.append(Finding("ERROR", "XAML_DUP_KEY_IN_FILE", rel, 0,
                                        f"x:Key '{k}' defined {n}x in one dictionary (last wins, earlier styles silently dead)"))
    seen: dict[str, list[str]] = {}
    for target in merged:
        rel = str(target.relative_to(app_dir))
        for k in re.findall(r'x:Key="([^"]+)"', texts.get(target, "")):
            seen.setdefault(k, []).append(rel)
    for k, files in seen.items():
        if len(files) > 1:
            findings.append(Finding("WARNING", "XAML_DUP_KEY_ACROSS_MERGED", files[0], 0,
                                    f"key '{k}' defined in multiple merged dictionaries ({', '.join(files)}) — later one silently wins"))

    # ── 7b. Theme key parity (Phase 7.1 review fix L2) ──
    # ThemeService swaps Light.xaml for Dark.xaml at RUNTIME (pack URI). The
    # audit models the default Light state; if the two palettes ever diverge,
    # a DynamicResource resolving under only one theme silently renders null
    # in the other. Key-set equality is the cheap guard-rail.
    light_p = app_dir / "Themes" / "Light.xaml"
    dark_p = app_dir / "Themes" / "Dark.xaml"
    if light_p.exists() and dark_p.exists() and texts.get(light_p) and texts.get(dark_p):
        lk = set(re.findall(r'x:Key="([^"]+)"', texts[light_p]))
        dk = set(re.findall(r'x:Key="([^"]+)"', texts[dark_p]))
        if lk != dk:
            findings.append(Finding("WARNING", "XAML_THEME_KEY_DIVERGENCE",
                                    str(dark_p.relative_to(app_dir)), 0,
                                    f"Dark/Light key sets differ: only-in-Light={sorted(lk - dk)} "
                                    f"only-in-Dark={sorted(dk - lk)} — verify every DynamicResource "
                                    f"reference under BOTH themes"))

    # ── 8. Converter / custom-class existence (xmlns clr-namespace elements) ──
    xmlns_re = re.compile(r'xmlns:([\w]+)="clr-namespace:([\w.]+)(?:;assembly=([\w.]+))?"')
    elem_re = re.compile(r"<([a-zA-Z][\w]*:[\w.]+)\b")
    for p in xamls:
        rel = str(p.relative_to(app_dir))
        text = texts[p]
        namespaces = {prefix: ns for prefix, ns, _asm in xmlns_re.findall(text)}
        if not namespaces:
            continue
        for m in elem_re.finditer(text):
            tag = m.group(1)
            if ":" not in tag:
                continue
            prefix, cls = tag.split(":", 1)
            ns = namespaces.get(prefix)
            if not ns or ns.startswith("System"):  # framework namespaces — out of scope
                continue
            full = f"{ns}.{cls}"
            if full not in corpus and cls not in corpus:
                findings.append(Finding("ERROR", "XAML_CLASS_MISSING", rel, _line_of(text, m.start()),
                                        f"<{tag}> resolves to '{full}' which does not exist in the source corpus"))

    return findings


def write_report(findings: list[Finding], path: Path) -> None:
    errors = [f for f in findings if f.severity == "ERROR"]
    warnings = [f for f in findings if f.severity == "WARNING"]
    lines = [
        "PrintEXE XAML static audit report",
        "=" * 34,
        f"findings: {len(errors)} error(s), {len(warnings)} warning(s), "
        f"{len(findings) - len(errors) - len(warnings)} info",
        "",
    ]
    lines += [f.format() for f in sorted(findings, key=lambda f: (f.severity != "ERROR", f.file, f.line))]
    if not findings:
        lines.append("(clean — no findings)")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main(argv: list[str]) -> int:
    # Phase 7.1 review fix M4/L6: guarded, documented argument handling.
    report = None
    app_dir_arg: str | None = None
    i = 0
    while i < len(argv):
        if argv[i] == "--report":
            if i + 1 >= len(argv):
                print("--report requires a file path", file=sys.stderr)
                return 2
            report = Path(argv[i + 1])
            i += 2
        elif argv[i].startswith("--"):
            print(f"unknown option: {argv[i]} (usage: xaml_audit.py <app-dir> [--report FILE])",
                  file=sys.stderr)
            return 2
        elif app_dir_arg is None:
            app_dir_arg = argv[i]
            i += 1
        else:
            print(f"unexpected extra argument: {argv[i]}", file=sys.stderr)
            return 2
    if app_dir_arg is None:
        print("usage: xaml_audit.py <app-dir> [--report FILE]", file=sys.stderr)
        return 2
    app_dir = Path(app_dir_arg)
    findings = audit(app_dir)
    for f in sorted(findings, key=lambda x: (x.severity != "ERROR", x.file, x.line)):
        print(f.format())
    errors = [f for f in findings if f.severity == "ERROR"]
    warnings = [f for f in findings if f.severity == "WARNING"]
    print(f"\nsummary: {len(errors)} error(s), {len(warnings)} warning(s), {len(findings)} finding(s)")
    if report:
        write_report(findings, report)
        print(f"report written: {report}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
