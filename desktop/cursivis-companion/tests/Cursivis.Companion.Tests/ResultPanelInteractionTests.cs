using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Cursivis.Companion.Controllers;
using Cursivis.Companion.Views;
using Xunit;

namespace Cursivis.Companion.Tests;

public sealed class ResultPanelInteractionTests
{
    [Fact]
    public void BrowserExtensionRecoveryShowsTheCurrentUsersBundledFolderAndSteps()
    {
        var hint = TriggerController.BuildBrowserExtensionSetupHint(@"D:\Users\QA Tester\AppData\Local");

        Assert.Contains("is not connected", hint);
        Assert.Contains("included with Companion", hint);
        Assert.Contains("chrome://extensions", hint);
        Assert.Contains("edge://extensions", hint);
        Assert.Contains("Developer mode", hint);
        Assert.Contains("Load unpacked", hint);
        Assert.Contains(@"`D:\Users\QA Tester\AppData\Local\Programs\Cursivis\desktop\browser-extension-chromium`", hint);
        Assert.Contains("Refresh the target tab and retry Take Action", hint);
    }

    [Fact]
    public async Task FormattedAndInteractiveContentDoesNotStartWindowDragging()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var linkText = new Run("Link");
                var link = new Hyperlink(linkText);
                var boldText = new Run("Bold");
                var paragraph = new Paragraph(new Bold(boldText));
                paragraph.Inlines.Add(link);
                var document = new FlowDocument(paragraph);
                var editor = new RichTextBox(document) { IsReadOnly = true };

                Assert.False(ResultPanelWindow.CanDragFrom(boldText));
                Assert.False(ResultPanelWindow.CanDragFrom(paragraph));
                Assert.False(ResultPanelWindow.CanDragFrom(link));
                Assert.False(ResultPanelWindow.CanDragFrom(linkText));
                Assert.False(ResultPanelWindow.CanDragFrom(editor));
                Assert.False(ResultPanelWindow.CanDragFrom(new ScrollBar()));
                Assert.False(ResultPanelWindow.CanDragFrom(new Thumb()));
                var icon = new Border();
                _ = new Button { Content = icon };
                Assert.False(ResultPanelWindow.CanDragFrom(icon));
                Assert.True(ResultPanelWindow.CanDragFrom(new Border()));
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
