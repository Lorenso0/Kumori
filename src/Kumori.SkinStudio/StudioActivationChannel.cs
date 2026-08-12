using osu.Framework.Platform;

namespace Kumori.SkinStudio;

internal sealed class StudioActivationChannel : IpcChannel<StudioActivationMessage>
{
    public StudioActivationChannel(
        IIpcHost host,
        Action<StudioActivationMessage>? handler = null)
        : base(host)
    {
        if (handler is not null)
        {
            MessageReceived += message =>
            {
                handler(message);
                return null;
            };
        }
    }

    public Task ActivateAsync(StudioActivationMessage message) =>
        SendMessageAsync(message);
}

internal sealed class StudioActivationMessage
{
    public string? ContractPath { get; set; }
}
