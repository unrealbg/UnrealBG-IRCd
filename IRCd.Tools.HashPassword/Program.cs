using System.Globalization;
using IRCd.Core.Security;

var (password, iterations) = ParseArgs(args);

if (password is null)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project IRCd.Tools.HashPassword -c Release -- --password <value> [--iterations N]");
    Console.Error.WriteLine("  echo <value> | dotnet run --project IRCd.Tools.HashPassword -c Release -- --stdin [--iterations N]");
    Environment.ExitCode = 2;
    return;
}

var hashed = Pbkdf2OperPasswordHasher.Hash(password, iterations);

Console.WriteLine(hashed);

static (string? Password, int? Iterations) ParseArgs(string[] args)
{
    string? password = null;
    var stdin = false;
    int? iterations = null;

    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a is "--password" or "-p")
        {
            password = i + 1 < args.Length ? args[++i] : null;
        }
        else if (a is "--stdin")
        {
            stdin = true;
        }
        else if (a is "--iterations")
        {
            iterations = int.Parse(args[++i], CultureInfo.InvariantCulture);
        }
    }

    if (stdin)
    {
        var line = Console.In.ReadLine();
        if (!string.IsNullOrEmpty(line))
            password = line;
    }

    return (password, iterations);
}
