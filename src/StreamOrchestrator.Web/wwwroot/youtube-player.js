// Thin wrapper around the official YouTube IFrame Player API:
// https://developers.google.com/youtube/iframe_api_reference
// It only forwards player events to the Blazor component. It never starts playback.

let apiReady;
let player;
let timer;

function loadApi() {
    if (window.YT && window.YT.Player) {
        return Promise.resolve();
    }

    apiReady ??= new Promise((resolve, reject) => {
        window.onYouTubeIframeAPIReady = resolve;
        const script = document.createElement("script");
        script.src = "https://www.youtube.com/iframe_api";
        script.onerror = () => reject(new Error("The YouTube IFrame API could not be loaded."));
        document.head.appendChild(script);
    });
    return apiReady;
}

function stopTimer() {
    clearInterval(timer);
    timer = undefined;
}

export async function create(elementId, videoId, dotNetRef) {
    await loadApi();

    const times = () => [player.getCurrentTime(), player.getDuration()];

    player = new YT.Player(elementId, {
        videoId,
        width: "100%",
        height: "100%",
        playerVars: { autoplay: 0, playsinline: 1, rel: 0, origin: window.location.origin },
        events: {
            onReady: () => dotNetRef.invokeMethodAsync("OnReady", player.getDuration()),
            onStateChange: event => {
                stopTimer();
                dotNetRef.invokeMethodAsync("OnStateChange", event.data, ...times());
                if (event.data === YT.PlayerState.PLAYING) {
                    timer = setInterval(() => dotNetRef.invokeMethodAsync("OnProgress", ...times()), 500);
                }
            },
            onError: event => dotNetRef.invokeMethodAsync("OnError", event.data)
        }
    });
}

export function dispose() {
    stopTimer();
    player?.destroy();
    player = undefined;
}
