using A2Utils.Cli;

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return CliApplication.Run(args, Console.Out, Console.Error, cancellation.Token);
