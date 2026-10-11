using System.Reflection;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

switch (args)
{
    case ["--version" or "version"]:
        Console.WriteLine($"terrascale {version}");
        return 0;
    case [] or ["--help" or "-h" or "help"]:
        Console.WriteLine("""
            terrascale: the TerraScale command-line client

            Usage: terrascale <command>

            Commands:
              version    Print the CLI version
              help       Show this help
            """);
        return 0;
    default:
        Console.Error.WriteLine($"terrascale: unknown command '{args[0]}'. Run 'terrascale help'.");
        return 1;
}
