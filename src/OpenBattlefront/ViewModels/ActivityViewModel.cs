using System.Windows.Input;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

/// <summary>The one long job that can run at a time (import, update, rebuild), and its progress.</summary>
public sealed class ActivityViewModel : ObservableObject
{
    private bool _isBusy;
    private string _title = "";
    private string? _detail;
    private double _fraction;
    private bool _isIndeterminate = true;
    private bool _canCancel;
    private CancellationTokenSource? _cts;

    public ActivityViewModel()
    {
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _canCancel && _cts is { IsCancellationRequested: false });
    }

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string? Detail { get => _detail; private set => Set(ref _detail, value); }
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public bool CanCancel { get => _canCancel; private set => Set(ref _canCancel, value); }
    public ICommand CancelCommand { get; }

    /// <summary>Runs a job with progress. Returns null when it finished, otherwise the error
    /// (an <see cref="OperationCanceledException"/> when the player cancelled).</summary>
    public async Task<Exception?> RunAsync(string title, Func<IProgress<StepUpdate>, CancellationToken, Task> job, bool cancellable = false)
    {
        if (IsBusy) return new InvalidOperationException("Something else is still running.");
        _cts = new CancellationTokenSource();
        Title = title;
        Detail = null;
        IsIndeterminate = true;
        Fraction = 0;
        CanCancel = cancellable;
        IsBusy = true;
        var progress = new Progress<StepUpdate>(u =>
        {
            Title = u.Title;
            Detail = u.Detail;
            IsIndeterminate = u.Fraction == null;
            Fraction = u.Fraction ?? 0;
        });
        try
        {
            await job(progress, _cts.Token);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            _cts.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
