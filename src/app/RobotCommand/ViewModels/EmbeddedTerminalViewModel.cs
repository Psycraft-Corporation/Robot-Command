using System.Text;
using System.Windows.Input;
using RobotCommand.Cli;
using RobotCommand.Infrastructure;

namespace RobotCommand.ViewModels;

/// <summary>Presentation adapter for the in-process Robot Command CLI terminal.</summary>
public sealed class EmbeddedTerminalViewModel : ObservableObject, IEmbeddedCliTerminalOutput, IDisposable
{
    private readonly EmbeddedCliTerminalSession _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly StringBuilder _output = new();
    private readonly List<string> _history = [];
    private string _input = "";
    private string _outputText = "";
    private int _historyIndex;
    private bool _disposed;

    public EmbeddedTerminalViewModel(IServiceProvider services, IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _session = new EmbeddedCliTerminalSession(services, this);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => !string.IsNullOrWhiteSpace(Input));
        ClearCommand = new RelayCommand(_ => Clear());
    }

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value))
                ((AsyncRelayCommand)ExecuteCommand).RaiseCanExecuteChanged();
        }
    }

    public string OutputText
    {
        get => _outputText;
        private set => SetProperty(ref _outputText, value);
    }

    public ICommand ExecuteCommand { get; }
    public ICommand ClearCommand { get; }

    public void WriteWelcome()
    {
        if (_output.Length > 0)
            return;

        _session.WriteWelcome();
    }

    public void RecallPrevious()
    {
        if (_history.Count == 0)
            return;

        _historyIndex = Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    public void RecallNext()
    {
        if (_history.Count == 0)
            return;

        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        Input = _historyIndex == _history.Count ? "" : _history[_historyIndex];
    }

    public void WriteOutput(string line) => Append(line);
    public void WriteError(string line) => Append($"Error: {line}");

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var command = Input.Trim();
        if (command.Length == 0)
            return;

        Append($"> {command}");
        _history.Add(command);
        _historyIndex = _history.Count;
        Input = "";

        try
        {
            var keepRunning = await _session.ExecuteAsync(command, cancellationToken);
            if (!keepRunning)
                Append("The terminal command session was stopped. Robot Command remains running; enter another command to continue.");
        }
        catch (Exception exception)
        {
            WriteError(exception.Message);
        }
    }

    private void Clear()
    {
        _output.Clear();
        OutputText = "";
    }

    private void Append(string line)
    {
        if (_disposed)
            return;

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => Append(line));
            return;
        }

        const int maximumCharacters = 80_000;
        _output.AppendLine(line);
        if (_output.Length > maximumCharacters)
            _output.Remove(0, _output.Length - maximumCharacters);
        OutputText = _output.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Dispose();
    }
}
