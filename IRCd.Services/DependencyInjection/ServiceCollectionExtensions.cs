namespace IRCd.Services.DependencyInjection
{
    using System;
    using System.IO;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Config;
    using IRCd.Core.Protocol;
    using IRCd.Core.Services;
    using IRCd.Shared.Options;
    using IRCd.Services.Agent;
    using IRCd.Services.Auth;
    using IRCd.Services.AdminServ;
    using IRCd.Services.BotServ;
    using IRCd.Services.ChanServ;
    using IRCd.Services.DevServ;
    using IRCd.Services.Dispatching;
    using IRCd.Services.Email;
    using IRCd.Services.HelpServ;
    using IRCd.Services.HostServ;
    using IRCd.Services.InfoServ;
    using IRCd.Services.MemoServ;
    using IRCd.Services.NickServ;
    using IRCd.Services.OperServ;
    using IRCd.Services.RootServ;
    using IRCd.Services.StatServ;
    using IRCd.Services.SeenServ;
    using IRCd.Services.Storage;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;

    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddIrcServices(this IServiceCollection services)
        {
            services.AddLogging();

            services.TryAddSingleton<IrcOptionsStore>(sp =>
            {
                var current = sp.GetService<IOptions<IrcOptions>>()?.Value;
                if (current is null)
                {
                    current = new IrcOptions();
                }

                return new IrcOptionsStore(current);
            });

            services.TryAddSingleton<IOptionsMonitor<IrcOptions>>(sp => sp.GetRequiredService<IrcOptionsStore>());

            services.TryAddSingleton<IrcConfigManager>(sp =>
            {
                var store = sp.GetRequiredService<IrcOptionsStore>();
                var env = sp.GetService<IHostEnvironment>() ?? new FallbackHostEnvironment();
                var listeners = sp.GetServices<IConfigReloadListener>() ?? Array.Empty<IConfigReloadListener>();
                var logger = sp.GetService<ILogger<IrcConfigManager>>() ?? NullLogger<IrcConfigManager>.Instance;
                return new IrcConfigManager(store, env, listeners, logger);
            });

            services.TryAddSingleton<IrcFormatter>();
            services.TryAddSingleton<RoutingService>();

            services.TryAddSingleton<InMemoryBanRepository>();
            services.TryAddSingleton<IBanRepository>(sp => sp.GetRequiredService<InMemoryBanRepository>());
            services.TryAddSingleton<BanService>();

            services.TryAddSingleton<FloodService>();

            services.TryAddSingleton<RuntimeKLineService>();
            services.TryAddSingleton<RuntimeDLineService>();
            services.TryAddSingleton<RuntimeDenyService>();
            services.TryAddSingleton<RuntimeWarnService>();
            services.TryAddSingleton<RuntimeTriggerService>();

            static string ResolvePath(IServiceProvider sp, string path)
            {
                if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                {
                    return path;
                }

                var env = sp.GetService<IHostEnvironment>();
                if (env is null || string.IsNullOrWhiteSpace(env.ContentRootPath))
                {
                    return path;
                }

                return Path.Combine(env.ContentRootPath, path);
            }

            services.AddSingleton<INickAccountRepository>(sp =>
            {
                var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>()?.Value;
                var path = opts?.Services?.NickServ?.AccountsFilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new InMemoryNickAccountRepository();
                }

                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<FileNickAccountRepository>>();
                var persistence = opts?.Services?.Persistence;
                return new FileNickAccountRepository(ResolvePath(sp, path), persistence, logger);
            });

            services.AddSingleton<IChanServChannelRepository>(sp =>
            {
                var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>()?.Value;
                var path = opts?.Services?.ChanServ?.ChannelsFilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new InMemoryChanServChannelRepository();
                }

                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<FileChanServChannelRepository>>();
                var persistence = opts?.Services?.Persistence;
                return new FileChanServChannelRepository(ResolvePath(sp, path), persistence, logger);
            });

            services.AddSingleton<IMemoRepository>(sp =>
            {
                var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>()?.Value;
                var path = opts?.Services?.MemoServ?.MemosFilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new InMemoryMemoRepository();
                }

                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<FileMemoRepository>>();
                var persistence = opts?.Services?.Persistence;
                return new FileMemoRepository(ResolvePath(sp, path), persistence, logger);
            });

            services.AddSingleton<ISeenRepository>(sp =>
            {
                var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>()?.Value;
                var path = opts?.Services?.SeenServ?.SeenFilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new InMemorySeenRepository();
                }

                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<FileSeenRepository>>();
                var persistence = opts?.Services?.Persistence;
                return new FileSeenRepository(ResolvePath(sp, path), persistence, logger);
            });

            services.AddSingleton<IAdminStaffRepository>(sp =>
            {
                var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>()?.Value;
                var path = opts?.Services?.AdminServ?.StaffFilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new InMemoryAdminStaffRepository();
                }

                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<FileAdminStaffRepository>>();
                var persistence = opts?.Services?.Persistence;
                return new FileAdminStaffRepository(ResolvePath(sp, path), persistence, logger);
            });

            services.AddSingleton<IVHostRepository>(sp =>
            {
                var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>()?.Value;
                var path = opts?.Services?.HostServ?.VHostsFilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new InMemoryVHostRepository();
                }

                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<FileVHostRepository>>();
                var persistence = opts?.Services?.Persistence;
                return new FileVHostRepository(ResolvePath(sp, path), persistence, logger);
            });
            services.AddSingleton<IAuthState, InMemoryAuthState>();

            services.AddSingleton<ISaslPlainAuthenticator, NickServSaslPlainAuthenticator>();

            services.TryAddSingleton<BotAssignmentService>();

            services.TryAddSingleton<AgentStateService>(sp =>
            {
                var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>().Value;
                return new AgentStateService(opts);
            });

            services.TryAddSingleton<ChannelSnoopService>();

            services.TryAddSingleton<IEmailSender>(sp =>
            {
                var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>()?.Value;
                var smtp = opts?.Services?.NickServ?.Smtp;
                if (smtp is not null && !string.IsNullOrWhiteSpace(smtp.Host) && !string.IsNullOrWhiteSpace(smtp.FromAddress))
                {
                    var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<SmtpEmailSender>>();
                    return new SmtpEmailSender(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions>>(), logger);
                }

                return new NullEmailSender();
            });

            services.AddSingleton<NickServService>();
            services.AddSingleton<ChanServService>();
            services.AddSingleton<OperServService>();
            services.AddSingleton<MemoServService>();
            services.AddSingleton<SeenServService>();
            services.AddSingleton<InfoServService>();
            services.AddSingleton<StatServService>();
            services.AddSingleton<AdminServService>();
            services.AddSingleton<DevServService>();
            services.AddSingleton<HelpServService>();
            services.AddSingleton<RootServService>();
            services.AddSingleton<HostServService>();
            services.AddSingleton<BotServService>();
            services.AddSingleton<AgentService>();

            services.AddSingleton<ServicesDispatcher>();
            services.AddSingleton<IServiceCommandDispatcher>(sp => sp.GetRequiredService<ServicesDispatcher>());

            services.AddSingleton<ServicesSessionEvents>();
            services.AddSingleton<IServiceSessionEvents>(sp => sp.GetRequiredService<ServicesSessionEvents>());

            services.AddSingleton<ServicesChannelEvents>();
            services.AddSingleton<IServiceChannelEvents>(sp => sp.GetRequiredService<ServicesChannelEvents>());

            return services;
        }

        private sealed class FallbackHostEnvironment : IHostEnvironment
        {
            public FallbackHostEnvironment()
            {
                ContentRootPath = Directory.GetCurrentDirectory();
                EnvironmentName = "Production";
                ApplicationName = "IRCd";
            }

            public string EnvironmentName { get; set; }
            public string ApplicationName { get; set; }
            public string ContentRootPath { get; set; }

            public IFileProvider ContentRootFileProvider
            {
                get => throw new NotImplementedException();
                set => throw new NotImplementedException();
            }
        }
    }
}
