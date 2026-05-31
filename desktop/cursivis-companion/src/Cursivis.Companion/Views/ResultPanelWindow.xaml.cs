using Cursivis.Companion.Infrastructure;
using Cursivis.Companion.Models;
using Cursivis.Companion.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Cursivis.Companion.Views;

public partial class ResultPanelWindow : Window
{
    private const string ThemeSunDarkIconPath = @"C:\Users\Admin\Downloads\theme-sun-dark.png";
    private const string SettingsGearDarkIconPath = @"C:\Users\Admin\Downloads\settings-gear-dark.png";

    private static readonly Regex HeadingRegex = new(@"^\s{0,3}(#{1,6})\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^\s*[-*]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRegex = new(@"^\s*\d+\.\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex MathLineRegex = new(@"(\\[A-Za-z]+|\$|[\^_]|=|≤|≥|≠|√)", RegexOptions.Compiled);
    private static readonly Regex FractionRegex = new(@"\\frac\{([^{}]+)\}\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex SqrtRegex = new(@"\\sqrt\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex TextRegex = new(@"\\text\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex InlineMathRegex = new(@"\$\$(.+?)\$\$|\$(.+?)\$|\\\((.+?)\\\)|\\\[(.+?)\\\]", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex SuperscriptRegex = new(@"(?<base>[A-Za-z0-9\)\]])\^(?<exp>\{[^{}]+\}|[A-Za-z0-9+\-=().]+)", RegexOptions.Compiled);
    private static readonly Regex SubscriptRegex = new(@"(?<base>[A-Za-z0-9\)\]])_(?<sub>\{[^{}]+\}|[A-Za-z0-9+\-=().]+)", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex InlineCodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex RevealTokenRegex = new(@"\S+\s*|\n", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> LatexTokenMap = new(StringComparer.Ordinal)
    {
        ["\\alpha"] = "alpha",
        ["\\beta"] = "beta",
        ["\\gamma"] = "gamma",
        ["\\delta"] = "delta",
        ["\\theta"] = "theta",
        ["\\lambda"] = "lambda",
        ["\\mu"] = "mu",
        ["\\pi"] = "pi",
        ["\\sigma"] = "sigma",
        ["\\phi"] = "phi",
        ["\\omega"] = "omega",
        ["\\times"] = "\u00D7",
        ["\\cdot"] = "\u00B7",
        ["\\leq"] = "\u2264",
        ["\\geq"] = "\u2265",
        ["\\neq"] = "\u2260",
        ["\\approx"] = "\u2248",
        ["\\pm"] = "\u00B1",
        ["\\sum"] = "\u2211",
        ["\\prod"] = "\u220F",
        ["\\infty"] = "\u221E",
        ["\\rightarrow"] = "\u2192",
        ["\\Rightarrow"] = "\u21D2"
    };

    private static readonly Dictionary<char, char> SuperscriptMap = new()
    {
        ['0'] = '\u2070',
        ['1'] = '\u00B9',
        ['2'] = '\u00B2',
        ['3'] = '\u00B3',
        ['4'] = '\u2074',
        ['5'] = '\u2075',
        ['6'] = '\u2076',
        ['7'] = '\u2077',
        ['8'] = '\u2078',
        ['9'] = '\u2079',
        ['+'] = '\u207A',
        ['-'] = '\u207B',
        ['='] = '\u207C',
        ['('] = '\u207D',
        [')'] = '\u207E',
        ['n'] = '\u207F',
        ['i'] = '\u2071'
    };

    private static readonly Dictionary<char, char> SubscriptMap = new()
    {
        ['0'] = '\u2080',
        ['1'] = '\u2081',
        ['2'] = '\u2082',
        ['3'] = '\u2083',
        ['4'] = '\u2084',
        ['5'] = '\u2085',
        ['6'] = '\u2086',
        ['7'] = '\u2087',
        ['8'] = '\u2088',
        ['9'] = '\u2089',
        ['+'] = '\u208A',
        ['-'] = '\u208B',
        ['='] = '\u208C',
        ['('] = '\u208D',
        [')'] = '\u208E',
        ['a'] = '\u2090',
        ['e'] = '\u2091',
        ['h'] = '\u2095',
        ['i'] = '\u1D62',
        ['j'] = '\u2C7C',
        ['k'] = '\u2096',
        ['l'] = '\u2097',
        ['m'] = '\u2098',
        ['n'] = '\u2099',
        ['o'] = '\u2092',
        ['p'] = '\u209A',
        ['r'] = '\u1D63',
        ['s'] = '\u209B',
        ['t'] = '\u209C',
        ['u'] = '\u1D64',
        ['v'] = '\u1D65',
        ['x'] = '\u2093'
    };

    private bool _isUserPositioned;
    private bool _hasInitialPlacement;
    private bool _isHiding;
    private int _hideAnimationVersion;
    private CancellationTokenSource? _revealCts;
    private HwndSource? _hwndSource;
    private CompanionThemeMode _themeMode = CompanionThemeService.CurrentMode;

    public ResultPanelWindow()
    {
        InitializeComponent();
        CompanionThemeService.ThemeChanged += CompanionThemeServiceOnThemeChanged;
        SourceInitialized += ResultPanelWindow_OnSourceInitialized;
        Closed += (_, _) =>
        {
            CompanionThemeService.ThemeChanged -= CompanionThemeServiceOnThemeChanged;
            if (_hwndSource is not null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
        };
        Deactivated += (_, _) =>
        {
            if (IsVisible)
            {
                HidePanel();
            }
        };
        ApplyThemePresentation(_themeMode);
    }

    public event EventHandler? InsertRequested;

    public event EventHandler? MoreOptionsRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? TakeActionRequested;

    public event EventHandler<CompanionThemeMode>? ThemeToggleRequested;

    public event EventHandler? UndoRequested;

    public string LastResult { get; private set; } = string.Empty;

    public void ShowResult(string action, string output, Point cursor)
    {
        LastResult = output;
        TitleText.Text = "Cursivis";
        ActionText.Text = action;
        TakeActionButton.IsEnabled = true;
        TakeActionButton.Visibility = Visibility.Visible;

        PositionPanel(cursor);
        EnsureShown();
        StartPresentation(output);
    }

    public void SetUndoAvailable(bool isAvailable)
    {
        UndoButton.IsEnabled = isAvailable;
    }

    public void ShowInfo(string text, Point cursor, bool allowTakeAction = false)
    {
        LastResult = text;
        TitleText.Text = "Cursivis";
        ActionText.Text = allowTakeAction ? "Take Action Status" : "System Status";
        TakeActionButton.IsEnabled = allowTakeAction;
        TakeActionButton.Visibility = Visibility.Visible;

        PositionPanel(cursor);
        EnsureShown();
        StartPresentation(text);
    }

    public void HidePanel()
    {
        if (!IsVisible || _isHiding)
        {
            return;
        }

        _isHiding = true;
        var animationVersion = ++_hideAnimationVersion;

        RootCard.BeginAnimation(UIElement.OpacityProperty, null);
        PanelTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

        var fadeOut = new DoubleAnimation
        {
            From = RootCard.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(135),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        var slideOut = new DoubleAnimation
        {
            From = PanelTranslateTransform.Y,
            To = 10,
            Duration = TimeSpan.FromMilliseconds(135),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            if (animationVersion != _hideAnimationVersion)
            {
                return;
            }

            RootCard.BeginAnimation(UIElement.OpacityProperty, null);
            PanelTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            RootCard.Opacity = 1;
            PanelTranslateTransform.Y = 0;
            _isHiding = false;

            if (IsVisible)
            {
                Hide();
            }
        };

        RootCard.BeginAnimation(UIElement.OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
        PanelTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideOut, HandoffBehavior.SnapshotAndReplace);
    }

    public bool ContainsScreenPoint(Point screenPoint)
    {
        if (!IsVisible)
        {
            return false;
        }

        var localPoint = PointFromScreen(screenPoint);
        return localPoint.X >= 0 &&
               localPoint.Y >= 0 &&
               localPoint.X <= ActualWidth &&
               localPoint.Y <= ActualHeight;
    }

    private void InsertButton_OnClick(object sender, RoutedEventArgs e)
    {
        InsertRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MoreOptionsButton_OnClick(object sender, RoutedEventArgs e)
    {
        MoreOptionsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TakeActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        TakeActionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ThemeToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        var nextMode = _themeMode == CompanionThemeMode.Dark
            ? CompanionThemeMode.Light
            : CompanionThemeMode.Dark;

        ThemeToggleRequested?.Invoke(this, nextMode);
    }

    private void UndoButton_OnClick(object sender, RoutedEventArgs e)
    {
        UndoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PositionPanel(Point cursor)
    {
        if (_isUserPositioned)
        {
            return;
        }

        if (!_hasInitialPlacement)
        {
            _hasInitialPlacement = true;
            Left = cursor.X + 55;
            Top = cursor.Y + 65;
        }

        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, Math.Min(Left, workArea.Right - Width - 8));
        Top = Math.Max(workArea.Top + 8, Math.Min(Top, workArea.Bottom - Height - 8));
    }

    private void RootCard_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            (FindParent<ButtonBase>(source) is not null ||
             FindParent<ScrollBar>(source) is not null ||
             FindParent<Thumb>(source) is not null))
        {
            return;
        }

        if (IsInResizeZone(e.GetPosition(this)))
        {
            return;
        }

        _isUserPositioned = true;
        try
        {
            DragMove();
        }
        catch
        {
            // Ignore drag interruption.
        }
    }

    private void EnsureShown()
    {
        _hideAnimationVersion++;
        _isHiding = false;
        RootCard.BeginAnimation(UIElement.OpacityProperty, null);
        PanelTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);

        if (!IsVisible)
        {
            RootCard.Opacity = 0;
            PanelTranslateTransform.Y = 18;
            Show();
        }
        else
        {
            RootCard.Opacity = 1;
            PanelTranslateTransform.Y = 0;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Topmost = true;
        Activate();
    }

    private void StartPresentation(string body)
    {
        UiPresentation.AnimateEntrance(RootCard, PanelTranslateTransform, fromY: 18, durationMs: 280);
        _ = PresentBodyAsync(body);
    }

    private async Task PresentBodyAsync(string body)
    {
        _revealCts?.Cancel();
        _revealCts?.Dispose();
        _revealCts = new CancellationTokenSource();
        var cancellationToken = _revealCts.Token;
        var normalizedBody = NormalizeNewlines(body);

        try
        {
            if (string.IsNullOrWhiteSpace(normalizedBody))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ResultDocumentBox.Document = BuildDocument(string.Empty);
                    ResultDocumentBox.CaretPosition = ResultDocumentBox.Document.ContentStart;
                    ResultDocumentBox.ScrollToHome();
                });
                return;
            }

            var revealTokens = RevealTokenRegex.Matches(normalizedBody)
                .Select(match => match.Value)
                .ToList();

            if (revealTokens.Count == 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ResultDocumentBox.Document = BuildDocument(normalizedBody);
                    ResultDocumentBox.CaretPosition = ResultDocumentBox.Document.ContentStart;
                    ResultDocumentBox.ScrollToHome();
                });
                return;
            }

            var step = revealTokens.Count switch
            {
                > 320 => 4,
                > 200 => 3,
                > 110 => 2,
                _ => 1
            };
            var delay = revealTokens.Count switch
            {
                > 320 => 12,
                > 200 => 14,
                > 110 => 17,
                _ => 20
            };
            var builder = new StringBuilder(normalizedBody.Length);

            for (var index = 0; index < revealTokens.Count; index += step)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var upperBound = Math.Min(index + step, revealTokens.Count);
                for (var tokenIndex = index; tokenIndex < upperBound; tokenIndex += 1)
                {
                    builder.Append(revealTokens[tokenIndex]);
                }

                var snapshot = builder.ToString();
                await Dispatcher.InvokeAsync(() =>
                {
                    ResultDocumentBox.Document = BuildDocument(snapshot);
                    ResultDocumentBox.CaretPosition = ResultDocumentBox.Document.ContentStart;
                });

                if (upperBound < revealTokens.Count)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                ResultDocumentBox.Document = BuildDocument(normalizedBody);
                ResultDocumentBox.CaretPosition = ResultDocumentBox.Document.ContentStart;
                ResultDocumentBox.ScrollToHome();
            });
        }
        catch (OperationCanceledException)
        {
            // No-op.
        }
    }

    private FlowDocument BuildDocument(string body)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = FindBrush("Brush.TextMain", "#FFF7FBFF"),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            LineHeight = 19,
            TextAlignment = TextAlignment.Left
        };

        var normalized = NormalizeNewlines(body);
        var lines = normalized.Split('\n');
        var pendingSpacing = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                pendingSpacing = true;
                continue;
            }

            var paragraph = BuildParagraph(line);
            if (pendingSpacing && document.Blocks.Count > 0)
            {
                paragraph.Margin = new Thickness(0, 8, 0, 0);
            }

            document.Blocks.Add(paragraph);
            pendingSpacing = false;
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run(string.Empty)));
        }

        return document;
    }

    private Paragraph BuildParagraph(string line)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 6)
        };

        var headingMatch = HeadingRegex.Match(line);
        if (headingMatch.Success)
        {
            var level = headingMatch.Groups[1].Value.Length;
            var headingText = CleanInlineMarkdown(headingMatch.Groups[2].Value);
            paragraph.Inlines.Add(new Bold(new Run(headingText)));
            paragraph.FontFamily = new FontFamily("Segoe UI Variable Display Semibold, Segoe UI Semibold");
            paragraph.FontSize = Math.Max(14, 19 - level);
            paragraph.Foreground = FindBrush("Brush.ResultHeadingText", "#FFF4F6F8");
            return paragraph;
        }

        var bulletMatch = BulletRegex.Match(line);
        if (bulletMatch.Success)
        {
            paragraph.Inlines.Add(new Run("\u2022 ")
            {
                Foreground = FindBrush("Brush.ResultBulletText", "#FFD4D9DF"),
                FontWeight = FontWeights.SemiBold
            });
            AppendFormattedInlines(paragraph.Inlines, bulletMatch.Groups[1].Value);
            return paragraph;
        }

        var numberedMatch = NumberedRegex.Match(line);
        if (numberedMatch.Success)
        {
            var prefix = line[..(line.IndexOf('.', StringComparison.Ordinal) + 1)] + " ";
            paragraph.Inlines.Add(new Run(prefix)
            {
                Foreground = FindBrush("Brush.ResultBulletText", "#FFD4D9DF"),
                FontWeight = FontWeights.SemiBold
            });
            AppendFormattedInlines(paragraph.Inlines, numberedMatch.Groups[1].Value);
            return paragraph;
        }

        if (LooksLikeMathLine(line))
        {
            paragraph.Background = FindBrush("Brush.ResultMathBackground", "#2E3D4B5B");
            paragraph.Padding = new Thickness(8, 4, 8, 4);
            paragraph.Margin = new Thickness(0, 2, 0, 8);
            AppendMathInline(paragraph.Inlines, line);
            return paragraph;
        }

        AppendFormattedInlines(paragraph.Inlines, line);
        return paragraph;
    }

    private void AppendFormattedInlines(InlineCollection inlines, string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var nextToken = FindNextToken(text, index);
            if (nextToken.Index > index)
            {
                inlines.Add(new Run(NormalizeLatexPlain(text[index..nextToken.Index])));
            }

            if (nextToken.Index < 0)
            {
                inlines.Add(new Run(NormalizeLatexPlain(text[index..])));
                break;
            }

            switch (nextToken.Kind)
            {
                case TokenKind.Bold:
                    var boldContent = text.Substring(nextToken.ContentStart, nextToken.ContentLength);
                    inlines.Add(new Bold(new Run(NormalizeLatexPlain(CleanInlineMarkdown(boldContent)))));
                    index = nextToken.NextIndex;
                    break;
                case TokenKind.Code:
                    var codeContent = text.Substring(nextToken.ContentStart, nextToken.ContentLength);
                    var codeSpan = new Span(new Run(codeContent))
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = FindBrush("Brush.ResultCodeBackground", "#26161A1F"),
                        Foreground = FindBrush("Brush.ResultCodeForeground", "#FFE7ECF2")
                    };
                    inlines.Add(codeSpan);
                    index = nextToken.NextIndex;
                    break;
                case TokenKind.Math:
                    var mathContent = text.Substring(nextToken.ContentStart, nextToken.ContentLength);
                    AppendMathInline(inlines, mathContent);
                    index = nextToken.NextIndex;
                    break;
                default:
                    index = text.Length;
                    break;
            }
        }
    }

    private void AppendMathInline(InlineCollection inlines, string mathContent)
    {
        var mathSpan = new Span(new Run(NormalizeLatexPlain(mathContent)))
        {
            FontFamily = new FontFamily("Cambria Math"),
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("Brush.ResultMathForeground", "#FFE6EAF0")
        };
        inlines.Add(mathSpan);
    }

    private static bool LooksLikeMathLine(string line)
    {
        return MathLineRegex.IsMatch(line);
    }

    private static string NormalizeLatexPlain(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var formatted = value;
        formatted = formatted.Replace("\r", string.Empty, StringComparison.Ordinal);
        formatted = formatted.Replace("\\(", string.Empty, StringComparison.Ordinal);
        formatted = formatted.Replace("\\)", string.Empty, StringComparison.Ordinal);
        formatted = formatted.Replace("\\[", string.Empty, StringComparison.Ordinal);
        formatted = formatted.Replace("\\]", string.Empty, StringComparison.Ordinal);
        formatted = formatted.Replace("$$", string.Empty, StringComparison.Ordinal);

        string previous;
        do
        {
            previous = formatted;
            formatted = FractionRegex.Replace(formatted, match =>
            {
                var numerator = NormalizeLatexPlain(match.Groups[1].Value);
                var denominator = NormalizeLatexPlain(match.Groups[2].Value);
                return $"({numerator})/({denominator})";
            });
            formatted = SqrtRegex.Replace(formatted, match => $"sqrt({NormalizeLatexPlain(match.Groups[1].Value)})");
            formatted = TextRegex.Replace(formatted, match => NormalizeLatexPlain(match.Groups[1].Value));
        }
        while (!string.Equals(previous, formatted, StringComparison.Ordinal));

        foreach (var token in LatexTokenMap)
        {
            formatted = formatted.Replace(token.Key, token.Value, StringComparison.Ordinal);
        }

        formatted = SuperscriptRegex.Replace(formatted, match =>
        {
            var baseValue = match.Groups["base"].Value;
            var exponent = TrimLatexBraces(match.Groups["exp"].Value);
            return baseValue + ConvertScript(exponent, SuperscriptMap);
        });

        formatted = SubscriptRegex.Replace(formatted, match =>
        {
            var baseValue = match.Groups["base"].Value;
            var subscript = TrimLatexBraces(match.Groups["sub"].Value);
            return baseValue + ConvertScript(subscript, SubscriptMap);
        });

        formatted = Regex.Replace(formatted, @"\{|\}", string.Empty);
        formatted = Regex.Replace(formatted, @"\s+", " ");
        return formatted.Trim();
    }

    private static string ConvertScript(string value, IReadOnlyDictionary<char, char> map)
    {
        var output = new List<char>(value.Length);
        foreach (var character in value)
        {
            if (map.TryGetValue(character, out var converted))
            {
                output.Add(converted);
            }
            else
            {
                output.Add(character);
            }
        }

        return new string(output.ToArray());
    }

    private static string TrimLatexBraces(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}')
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static string CleanInlineMarkdown(string value)
    {
        var cleaned = value;
        cleaned = cleaned.Replace("**", string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Replace("__", string.Empty, StringComparison.Ordinal);
        return cleaned.Trim();
    }

    private static string NormalizeNewlines(string body)
    {
        var normalized = body ?? string.Empty;
        normalized = normalized.Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized = normalized.Replace('\r', '\n');
        return normalized.Trim();
    }

    private static TokenMatch FindNextToken(string text, int startIndex)
    {
        TokenMatch best = TokenMatch.None;

        var boldMatch = BoldRegex.Match(text, startIndex);
        if (boldMatch.Success)
        {
            best = TokenMatch.From(TokenKind.Bold, boldMatch.Index, boldMatch.Length, boldMatch.Groups[1].Success ? boldMatch.Groups[1] : boldMatch.Groups[2]);
        }

        var codeMatch = InlineCodeRegex.Match(text, startIndex);
        if (codeMatch.Success && (!best.Found || codeMatch.Index < best.Index))
        {
            best = TokenMatch.From(TokenKind.Code, codeMatch.Index, codeMatch.Length, codeMatch.Groups[1]);
        }

        var mathMatch = InlineMathRegex.Match(text, startIndex);
        if (mathMatch.Success && (!best.Found || mathMatch.Index < best.Index))
        {
            var group = mathMatch.Groups.Cast<Group>().Skip(1).First(g => g.Success);
            best = TokenMatch.From(TokenKind.Math, mathMatch.Index, mathMatch.Length, group);
        }

        return best;
    }

    private Brush FindBrush(string resourceKey, string fallbackHex)
    {
        if (TryFindResource(resourceKey) is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(ColorFromHex(fallbackHex));
    }

    private void CompanionThemeServiceOnThemeChanged(object? sender, Models.CompanionThemeMode e)
    {
        _themeMode = e;
        ApplyThemePresentation(e);

        if (!IsLoaded || string.IsNullOrWhiteSpace(LastResult))
        {
            return;
        }

        void RefreshDocument()
        {
            ResultDocumentBox.Document = BuildDocument(LastResult);
            ResultDocumentBox.CaretPosition = ResultDocumentBox.Document.ContentStart;
            ResultDocumentBox.ScrollToHome();
        }

        if (Dispatcher.CheckAccess())
        {
            RefreshDocument();
            return;
        }

        Dispatcher.Invoke(RefreshDocument);
    }

    private void ApplyThemePresentation(CompanionThemeMode themeMode)
    {
        var baseColor = themeMode == CompanionThemeMode.Dark
            ? ColorFromHex("#FFC9D0D8")
            : ColorFromHex("#FF5A6067");
        var shineColor = themeMode == CompanionThemeMode.Dark
            ? ColorFromHex("#FFFFFFFF")
            : ColorFromHex("#FFD5DAE0");

        UiPresentation.ApplyShinyText(
            TitleText,
            baseColor,
            shineColor,
            themeMode == CompanionThemeMode.Dark ? 2.65 : 3.35,
            themeMode == CompanionThemeMode.Dark ? 0.94 : 0.32,
            themeMode == CompanionThemeMode.Dark ? 1.08 : 1.04);
        ApplyThemeToggleIcon(themeMode);
        ThemeToggleButton.ToolTip = themeMode == CompanionThemeMode.Dark
            ? "Switch to light mode"
            : "Switch to dark mode";
        ApplySettingsIcon(themeMode);
        SettingsButton.ToolTip = "Settings";
        ApplyHeaderButtonChrome(themeMode);
    }

    private void ApplyThemeToggleIcon(CompanionThemeMode themeMode)
    {
        if (themeMode == CompanionThemeMode.Dark)
        {
            if (TrySetButtonImage(ThemeToggleButton, ThemeSunDarkIconPath, 15, ColorFromHex("#FFF6F8FB")))
            {
                return;
            }

            ThemeToggleButton.FontFamily = new FontFamily("Segoe UI Symbol");
            ThemeToggleButton.Content = "\u2600";
            return;
        }

        ThemeToggleButton.FontFamily = new FontFamily("Segoe UI Symbol");
        ThemeToggleButton.Content = "\u263E";
    }

    private void ApplySettingsIcon(CompanionThemeMode themeMode)
    {
        var tintColor = themeMode == CompanionThemeMode.Dark
            ? ColorFromHex("#FFF6F8FB")
            : ColorFromHex("#FF1F252B");

        if (TrySetButtonImage(SettingsButton, SettingsGearDarkIconPath, 15, tintColor))
        {
            return;
        }

        SettingsButton.FontFamily = new FontFamily("Segoe UI Symbol");
        SettingsButton.Content = "\u2699";
    }

    private static bool TrySetButtonImage(Button button, string path, double size, Color tintColor)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var imageSource = LoadPreparedIconMask(path);
            if (imageSource is null)
            {
                return false;
            }

            button.FontFamily = new FontFamily("Segoe UI");
            button.Content = new Rectangle
            {
                Width = size,
                Height = size,
                Fill = CreateBrush(tintColor),
                OpacityMask = new ImageBrush(imageSource) { Stretch = Stretch.Uniform },
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static ImageSource? LoadPreparedIconMask(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = bitmap;
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var background = SampleBackgroundColor(pixels, stride, converted.PixelWidth, converted.PixelHeight);
        var maskPixels = new byte[pixels.Length];
        var minX = converted.PixelWidth;
        var minY = converted.PixelHeight;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var baseIndex = (y * stride) + (x * 4);
                var blue = pixels[baseIndex];
                var green = pixels[baseIndex + 1];
                var red = pixels[baseIndex + 2];
                var alpha = pixels[baseIndex + 3];

                var maskAlpha = alpha;
                if (maskAlpha <= 8)
                {
                    var difference = Math.Max(
                        Math.Abs(red - background.R),
                        Math.Max(
                            Math.Abs(green - background.G),
                            Math.Abs(blue - background.B)));

                    if (difference >= 18)
                    {
                        maskAlpha = (byte)Math.Min(255, 40 + (difference * 5));
                    }
                }

                if (maskAlpha <= 8)
                {
                    continue;
                }

                maskPixels[baseIndex] = 255;
                maskPixels[baseIndex + 1] = 255;
                maskPixels[baseIndex + 2] = 255;
                maskPixels[baseIndex + 3] = maskAlpha;

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return bitmap;
        }

        var maskBitmap = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            PixelFormats.Bgra32,
            null,
            maskPixels,
            stride);
        maskBitmap.Freeze();

        var padding = 6;
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(converted.PixelWidth - 1, maxX + padding);
        maxY = Math.Min(converted.PixelHeight - 1, maxY + padding);

        var cropWidth = maxX - minX + 1;
        var cropHeight = maxY - minY + 1;
        var cropped = new CroppedBitmap(maskBitmap, new Int32Rect(minX, minY, cropWidth, cropHeight));
        cropped.Freeze();
        return cropped;
    }

    private static Color SampleBackgroundColor(byte[] pixels, int stride, int width, int height)
    {
        var points = new[]
        {
            new Point(2, 2),
            new Point(Math.Max(2, width - 3), 2),
            new Point(2, Math.Max(2, height - 3)),
            new Point(Math.Max(2, width - 3), Math.Max(2, height - 3))
        };

        var red = 0;
        var green = 0;
        var blue = 0;

        foreach (var point in points)
        {
            var index = (((int)point.Y) * stride) + (((int)point.X) * 4);
            blue += pixels[index];
            green += pixels[index + 1];
            red += pixels[index + 2];
        }

        return Color.FromRgb(
            (byte)(red / points.Length),
            (byte)(green / points.Length),
            (byte)(blue / points.Length));
    }


    private void ApplyHeaderButtonChrome(CompanionThemeMode themeMode)
    {
        if (themeMode == CompanionThemeMode.Dark)
        {
            var pillBackground = CreateBrush("#F0101317");
            var iconBackground = CreateBrush("#EC0D1014");
            var borderBrush = CreateBrush("#24FFFFFF");
            var foreground = CreateBrush("#FFF5F7FA");

            ApplyButtonChrome(UndoButton, pillBackground, borderBrush, foreground);
            ApplyButtonChrome(InsertButton, pillBackground, borderBrush, foreground);
            ApplyButtonChrome(TakeActionButton, pillBackground, borderBrush, foreground);
            ApplyButtonChrome(MoreOptionsButton, pillBackground, borderBrush, foreground);
            ApplyButtonChrome(ThemeToggleButton, iconBackground, borderBrush, foreground);
            ApplyButtonChrome(SettingsButton, iconBackground, borderBrush, foreground);
            return;
        }

        ClearButtonChrome(UndoButton);
        ClearButtonChrome(InsertButton);
        ClearButtonChrome(TakeActionButton);
        ClearButtonChrome(MoreOptionsButton);
        ClearButtonChrome(ThemeToggleButton);
        ClearButtonChrome(SettingsButton);
    }

    private static void ApplyButtonChrome(ButtonBase button, Brush background, Brush borderBrush, Brush foreground)
    {
        button.SetCurrentValue(Control.BackgroundProperty, background);
        button.SetCurrentValue(Control.BorderBrushProperty, borderBrush);
        button.SetCurrentValue(Control.ForegroundProperty, foreground);
    }

    private static void ClearButtonChrome(ButtonBase button)
    {
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.ClearValue(Control.ForegroundProperty);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush(ColorFromHex(hex));
        brush.Freeze();
        return brush;
    }

    private void ResultPanelWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmNchittest = 0x0084;
        if (msg != wmNchittest || WindowState != WindowState.Normal)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return (IntPtr)HitTestResize(lParam);
    }

    private int HitTestResize(IntPtr lParam)
    {
        const int htClient = 1;
        const int htLeft = 10;
        const int htRight = 11;
        const int htTop = 12;
        const int htTopLeft = 13;
        const int htTopRight = 14;
        const int htBottom = 15;
        const int htBottomLeft = 16;
        const int htBottomRight = 17;

        var mouseScreen = GetScreenPoint(lParam);
        var point = PointFromScreen(mouseScreen);
        var frame = 14d;

        var onLeft = point.X <= frame;
        var onRight = point.X >= ActualWidth - frame;
        var onTop = point.Y <= frame;
        var onBottom = point.Y >= ActualHeight - frame;

        if (onTop && onLeft) return htTopLeft;
        if (onTop && onRight) return htTopRight;
        if (onBottom && onLeft) return htBottomLeft;
        if (onBottom && onRight) return htBottomRight;
        if (onLeft) return htLeft;
        if (onRight) return htRight;
        if (onTop) return htTop;
        if (onBottom) return htBottom;
        return htClient;
    }

    private bool IsInResizeZone(Point point)
    {
        const double frame = 14;
        return point.X <= frame ||
               point.X >= ActualWidth - frame ||
               point.Y <= frame ||
               point.Y >= ActualHeight - frame;
    }

    private static Point GetScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xFFFF));
        var y = unchecked((short)((value >> 16) & 0xFFFF));
        return new Point(x, y);
    }

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static Color ColorFromHex(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value);
    }

    private enum TokenKind
    {
        None,
        Bold,
        Code,
        Math
    }

    private readonly record struct TokenMatch(bool Found, TokenKind Kind, int Index, int ContentStart, int ContentLength, int NextIndex)
    {
        public static TokenMatch None => new(false, TokenKind.None, -1, -1, 0, -1);

        public static TokenMatch From(TokenKind kind, int index, int length, Group contentGroup)
        {
            return new TokenMatch(true, kind, index, contentGroup.Index, contentGroup.Length, index + length);
        }
    }
}
