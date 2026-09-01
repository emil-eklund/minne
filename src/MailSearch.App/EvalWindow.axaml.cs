using Avalonia.Controls;

namespace MailSearch.App;

/// <summary>Shows the plain-text report of an eval run (recall@k / MRR per retrieval mode).</summary>
public partial class EvalWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    public EvalWindow() => InitializeComponent();

    public EvalWindow(MainViewModel vm, string evalFile) : this()
    {
        Closed += (_, _) => _cts.Cancel();
        Opened += async (_, _) =>
        {
            try
            {
                Report.Text = await vm.RunEvalAsync(evalFile, _cts.Token);
            }
            catch (OperationCanceledException) { Report.Text = "Evaluation cancelled."; }
            catch (Exception ex)
            {
                Report.Text = $"Evaluation failed: {ex.Message}";
            }
        };
    }
}
