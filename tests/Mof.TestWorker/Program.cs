var input = await File.ReadAllTextAsync(args[0]);
await File.WriteAllTextAsync("started.pid", Environment.ProcessId.ToString());
if (input.Contains("#delay")) await Task.Delay(TimeSpan.FromMinutes(1));
if (input.Contains("#fail")) return 7;
if (input.Contains("#nooutput")) return 0;
if (input.Contains("#logs"))
{
    await Console.Out.WriteAsync(new string('o', 100000));
    await Console.Error.WriteAsync(new string('e', 100000));
}
await File.WriteAllTextAsync(args[1], input.Contains("#large") ? new string('x', 10000) : "v 0 0 0\nvt 0 0\nf 1/1 1/1 1/1\n");
return 0;
