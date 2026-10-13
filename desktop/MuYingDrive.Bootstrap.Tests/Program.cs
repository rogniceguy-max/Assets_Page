using MuYingDrive.Bootstrap.ViewModels;
using MuYingDrive.Bootstrap.Infrastructure;

// Regression harness for the setup window's command wiring.
//
// The bug this exists for: WPF queries ICommand.CanExecute once when the binding is
// applied and only asks again when the command raises CanExecuteChanged. The view model
// starts busy, so that first query answers "disabled" for every button — and because the
// commands never raised anything, the answer was never revisited. The success window
// rendered with both buttons permanently disabled, which in the disabled chrome of the
// default WPF template meant a dark theme's near-white label on a light system fill:
// a finished-looking window whose text was illegible and whose buttons did nothing.

var failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + name);
    if (!condition) failures++;
}

// A config path that cannot exist. LoadConfiguration rejects it before the engine touches
// the network, the install directory, or the settings file, so this exercises the window's
// terminal state without installing anything.
// Fully qualified: WPF brings System.Windows.Shapes.Path into implicit scope.
var missingConfig = System.IO.Path.Combine(
    System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.json");

var closed = false;
var model = new SetupViewModel(() => closed = true);

Console.WriteLine("SetupViewModel initial state");
Check("starts busy", model.IsBusy);
Check("close is unavailable while installing", !model.CloseCommand.CanExecute(null));
Check("launch is unavailable while installing", !model.LaunchCommand.CanExecute(null));
Check("cancel is available while installing", model.CancelCommand.CanExecute(null));

// The heart of the regression: the view has to be told to look again.
var closeRequeried = 0;
var launchRequeried = 0;
model.CloseCommand.CanExecuteChanged += (_, _) => closeRequeried++;
model.LaunchCommand.CanExecuteChanged += (_, _) => launchRequeried++;

await model.RunAsync(["--config", missingConfig]);

Console.WriteLine("SetupViewModel after a failed install");
Check("reaches a terminal state", !model.IsBusy);
Check("reports failure", model.HasFailed);
Check("close becomes available", model.CloseCommand.CanExecute(null));
Check("cancel becomes unavailable once finished", !model.CancelCommand.CanExecute(null));
Check("the close command raised CanExecuteChanged", closeRequeried > 0);
Check("the launch command raised CanExecuteChanged", launchRequeried > 0);

// A failed install has no client to launch, so this button must stay hidden and dead;
// WPF still asks, and the answer has to be a considered one rather than a stale one.
Check("launch stays unavailable after a failure", !model.LaunchCommand.CanExecute(null));

Console.WriteLine("SetupViewModel close command");
model.CloseCommand.Execute(null);
Check("close actually closes the window", closed);

var failed = failures != 0;
Console.WriteLine(failed ? $"{failures} CHECK(S) FAILED" : "ALL CHECKS PASSED");
return failed ? 1 : 0;
