using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using SerialLog.App;

namespace SerialLog.Tests;

public class MainWindowInputTests
{
    [Fact]
    public void Find_ancestor_handles_flow_document_content_elements()
    {
        RunInSta(() =>
        {
            var viewer = new RichTextBox();
            var paragraph = new Paragraph();
            var run = new Run("log line");
            paragraph.Inlines.Add(run);
            viewer.Document.Blocks.Add(paragraph);

            var method = typeof(MainWindow).GetMethod(
                "FindAncestor",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var result = method!
                .MakeGenericMethod(typeof(RichTextBox))
                .Invoke(null, [run]);

            Assert.Same(viewer, result);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw new Xunit.Sdk.XunitException(exception.ToString());
        }
    }
}
