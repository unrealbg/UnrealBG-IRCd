namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.Security;
    using IRCd.Core.Services;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class OperHandler : IIrcCommandHandler
    {
        public string Command => "OPER";

        private readonly IOptions<IrcOptions> _options;
        private readonly IOperPasswordVerifier _verifier;
        private readonly IAuditLogService _audit;

        public OperHandler(IOptions<IrcOptions> options, IOperPasswordVerifier verifier, IAuditLogService? audit = null)
        {
            _options = options;
            _verifier = verifier;
            _audit = audit ?? NullAuditLogService.Instance;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            if (msg.Params.Count < 2)
            {
                await session.SendAsync($":server 461 {session.Nick} OPER :Not enough parameters", ct);
                return;
            }

            var me = session.Nick!;

            state.TryGetUser(session.ConnectionId, out var user);
            var sourceIp = user?.RemoteIp ?? (session.RemoteEndPoint is IPEndPoint ip ? ip.Address.ToString() : null);
            var actorUid = user?.Uid;
            var actorNick = user?.Nick ?? me;

            var operName = msg.Params[0] ?? string.Empty;
            var provided = msg.Params[1] ?? string.Empty;

            var cfg = _options.Value;
            var requireHashed = cfg.OperSecurity?.RequireHashedPasswords == true;

            string? operClass = null;

            if (cfg.Opers is { Length: > 0 })
            {
                var match = cfg.Opers.FirstOrDefault(o =>
                    o is not null
                    && !string.IsNullOrWhiteSpace(o.Name)
                    && string.Equals(o.Name, operName, StringComparison.OrdinalIgnoreCase));

                if (match is null || string.IsNullOrWhiteSpace(match.Password))
                {
                    await session.SendAsync($":server 464 {me} :Password incorrect", ct);

                    await _audit.LogOperActionAsync(
                        action: "OPER",
                        session: session,
                        actorUid: actorUid,
                        actorNick: actorNick,
                        sourceIp: sourceIp,
                        target: operName,
                        reason: null,
                        extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "password_incorrect" },
                        ct: ct);
                    return;
                }

                var v = _verifier.Verify(provided, match.Password, requireHashed);
                if (!v.Success)
                {
                    if (v.Failure == OperPasswordVerifyFailure.PlaintextDisallowed)
                    {
                        await session.SendAsync($":server 464 {me} :Plaintext oper passwords are disabled; configure a hashed password", ct);

                        await _audit.LogOperActionAsync(
                            action: "OPER",
                            session: session,
                            actorUid: actorUid,
                            actorNick: actorNick,
                            sourceIp: sourceIp,
                            target: operName,
                            reason: null,
                            extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "plaintext_disallowed" },
                            ct: ct);
                        return;
                    }

                    await session.SendAsync($":server 464 {me} :Password incorrect", ct);

                    await _audit.LogOperActionAsync(
                        action: "OPER",
                        session: session,
                        actorUid: actorUid,
                        actorNick: actorNick,
                        sourceIp: sourceIp,
                        target: operName,
                        reason: null,
                        extra: new Dictionary<string, object?>
                        {
                            ["success"] = false,
                            ["error"] = v.Failure == OperPasswordVerifyFailure.InvalidHashFormat ? "invalid_hash_format" : "password_incorrect"
                        },
                        ct: ct);
                    return;
                }

                operClass = string.IsNullOrWhiteSpace(match.Class) ? null : match.Class;
            }
            else
            {
                var expected = cfg.OperPassword;
                if (string.IsNullOrWhiteSpace(expected))
                {
                    await session.SendAsync($":server 464 {me} :Password incorrect", ct);

                    await _audit.LogOperActionAsync(
                        action: "OPER",
                        session: session,
                        actorUid: actorUid,
                        actorNick: actorNick,
                        sourceIp: sourceIp,
                        target: operName,
                        reason: null,
                        extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "oper_password_not_configured" },
                        ct: ct);
                    return;
                }

                var v = _verifier.Verify(provided, expected, requireHashed);
                if (!v.Success)
                {
                    if (v.Failure == OperPasswordVerifyFailure.PlaintextDisallowed)
                    {
                        await session.SendAsync($":server 464 {me} :Plaintext oper passwords are disabled; configure a hashed password", ct);

                        await _audit.LogOperActionAsync(
                            action: "OPER",
                            session: session,
                            actorUid: actorUid,
                            actorNick: actorNick,
                            sourceIp: sourceIp,
                            target: operName,
                            reason: null,
                            extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "plaintext_disallowed" },
                            ct: ct);
                        return;
                    }

                    await session.SendAsync($":server 464 {me} :Password incorrect", ct);

                    await _audit.LogOperActionAsync(
                        action: "OPER",
                        session: session,
                        actorUid: actorUid,
                        actorNick: actorNick,
                        sourceIp: sourceIp,
                        target: operName,
                        reason: null,
                        extra: new Dictionary<string, object?>
                        {
                            ["success"] = false,
                            ["error"] = v.Failure == OperPasswordVerifyFailure.InvalidHashFormat ? "invalid_hash_format" : "password_incorrect"
                        },
                        ct: ct);
                    return;
                }
            }

            if (state.TrySetUserMode(session.ConnectionId, UserModes.Operator, enable: true))
            {
                state.TrySetOperInfo(session.ConnectionId, operName, operClass);
                await session.SendAsync($":server 381 {me} :You are now an IRC operator", ct);

                await _audit.LogOperActionAsync(
                    action: "OPER",
                    session: session,
                    actorUid: actorUid,
                    actorNick: actorNick,
                    sourceIp: sourceIp,
                    target: operName,
                    reason: null,
                    extra: new Dictionary<string, object?> { ["success"] = true, ["operClass"] = operClass },
                    ct: ct);
                return;
            }

            await _audit.LogOperActionAsync(
                action: "OPER",
                session: session,
                actorUid: actorUid,
                actorNick: actorNick,
                sourceIp: sourceIp,
                target: operName,
                reason: null,
                extra: new Dictionary<string, object?> { ["success"] = false, ["error"] = "state_update_failed" },
                ct: ct);
        }
    }
}
