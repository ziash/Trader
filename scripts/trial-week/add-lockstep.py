#!/usr/bin/env python3
"""Adds the lock-step switch to scratch copies of the Scout and TM builds that ran on IC Markets on 04-06 Nov 2026. Works on throw-away copies under the work folder; the repository and the running bots are not touched."""
import re, sys, pathlib

root = pathlib.Path(sys.argv[1])
head = pathlib.Path(sys.argv[2]).read_text(encoding='utf-8')


def sub(text, old, new, count=1):
    assert old in text, old
    return text.replace(old, new, count) if count else text.replace(old, new)


def lockstep(tree):
    text = head
    text = sub(text, 'public sealed class LockStep\n{', 'public sealed class LockStep\n{\n    public static LockStep? Current { get; private set; }\n    public static DateTime Now => Current?.UtcNow ?? DateTime.UtcNow;\n    public static DateTime ScoutNow => Current is null ? DateTime.UtcNow : Current.UtcNow + TimeSpan.FromSeconds(6); // the old Scout asks 5 s after a close')
    text = sub(text, '        return new LockStep();', '        return Current = new LockStep();')
    text = sub(text, '        session.UseReplayClock(() => UtcNow);\n', '')
    (tree / 'src/Trader.Core/OpenApi/LockStep.cs').write_text(text, encoding='utf-8')


JOIN = '''            if (lockStep is not null)
            {
                while (registry.RoleOn(a.AccountId) != InstanceRole.Primary)
                    await Task.Delay(100);
                await lockStep.JoinAsync(session, a.AccountId);
            }
'''

# Scout
tree = root / 'scout'
lockstep(tree)
p = tree / 'src/Trader.Scout/AccountScout.cs'
t = p.read_text(encoding='utf-8')
t = t.replace('DateTime.UtcNow', 'LockStep.ScoutNow')
t = sub(t, '            if (wait > TimeSpan.Zero)\n                await Task.Delay(wait);', '            if (wait > TimeSpan.Zero && LockStep.Current is null)\n                await Task.Delay(wait);')
t = sub(t, '''        while (true)
        {
            await _wake.WaitAsync(Cycle, ct);''', '''        while (true)
        {
            if (LockStep.Current is { } step)
            {
                await step.NextTurnAsync(ct);
                if (step.UtcNow.Ticks % TimeSpan.FromMinutes(5).Ticks == 0 && _session is { } s)
                {
                    try { await RunOnceAsync(s); }
                    catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
                    {
                        _log.Event("ScoutCycleError").Warning("{Broker}: {Message}; retrying", account.Broker, ex.Message);
                        _dirty = true;
                    }
                }
                await step.FinishTurnAsync();
                continue;
            }
            await _wake.WaitAsync(Cycle, ct);''')
if 'using Trader.Core.OpenApi;' not in t:
    t = 'using Trader.Core.OpenApi;\n' + t
p.write_text(t, encoding='utf-8')

p = tree / 'src/Trader.Scout/Program.cs'
t = p.read_text(encoding='utf-8')
line = '            log.Event("Subscribed").Information("{Broker}: watching {Symbols}", a.Broker, string.Join(", ", symbols.Select(x => x.Name)));\n'
t = sub(t, line, line + JOIN)
t = sub(t, '        scouts.Add(new AccountScout(', '        scouts.Add(new AccountScout(')
anchor = '    bot.Connection.OnReady = async (session, accounts, _) =>'
t = sub(t, anchor, '    var lockStep = LockStep.FromArgs(args, bot.SecretsPath);\n    if (lockStep is not null)\n        bot.Connection.MessageReceived += lockStep.Handle;\n' + anchor)
if 'using Trader.Core.OpenApi;' not in t:
    t = 'using Trader.Core.OpenApi;\n' + t
p.write_text(t, encoding='utf-8')

# TM
tree = root / 'tm'
lockstep(tree)
p = tree / 'src/Trader.TM/AccountManager.cs'
t = p.read_text(encoding='utf-8')
t = t.replace('DateTime.UtcNow', 'LockStep.Now')
t = sub(t, '''        while (true)
        {
            await _wake.WaitAsync(Cycle, ct);''', '''        while (true)
        {
            if (LockStep.Current is { } step)
            {
                await step.NextTurnAsync(ct);
                if (_session is { } s && !s.Closed.IsCompleted)
                {
                    try
                    {
                        var runs = 0;
                        do { await RunOnceAsync(s); } while (_dirty && ++runs < 8);
                    }
                    catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
                    {
                        _log.Event("TmCycleError").Warning("{Broker}: {Message}; retrying", account.Broker, ex.Message);
                        _dirty = true;
                    }
                }
                await step.FinishTurnAsync();
                continue;
            }
            await _wake.WaitAsync(Cycle, ct);''')
if 'using Trader.Core.OpenApi;' not in t:
    t = 'using Trader.Core.OpenApi;\n' + t
p.write_text(t, encoding='utf-8')

p = tree / 'src/Trader.TM/Program.cs'
t = p.read_text(encoding='utf-8')
t = sub(t, '    bot.Connection.MessageReceived += feed.Handle;\n', '    var lockStep = LockStep.FromArgs(args, bot.SecretsPath);\n    if (lockStep is not null)\n        bot.Connection.MessageReceived += lockStep.Handle;\n    bot.Connection.MessageReceived += feed.Handle;\n')
line = '            log.Event("Subscribed").Information("{Broker}: watching {Symbols}", a.Broker, string.Join(", ", symbols.Select(s => s.Name)));\n'
t = sub(t, line, line + JOIN)
t = sub(t, 'c => alerts.RunAsync(bot.Notify, c)', 'c => lockStep is null ? alerts.RunAsync(bot.Notify, c) : Task.Delay(Timeout.Infinite, c)')
if 'using Trader.Core.OpenApi;' not in t:
    t = 'using Trader.Core.OpenApi;\n' + t
p.write_text(t, encoding='utf-8')
print('patched')
