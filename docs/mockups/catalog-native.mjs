export default {
  pages: [],
  dialogs: [
    {
      id:'native-startup-error', title:'TogetherServer could not open', area:'app',
      description:'Startup fallback when the application interface is unavailable.',
      states:['normal','window-failed','data-unavailable','data-rejected','local-gui-failed','link-failed'],
      html:`<div class="ts-alert" data-tone="error">TogetherServer could not open its window.</div><p>Close the failed instance and try again. No server action was started by this message.</p><p class="ts-muted ts-small">Windows retains a native fallback message if the bundled interface cannot be displayed.</p>`,
      variants:{
        'window-failed':`<div class="ts-alert" data-tone="error">TogetherServer could not open its window.</div><p>The local interface remains unavailable. Review the startup error before trying again.</p>`,
        'data-unavailable':`<div class="ts-alert" data-tone="error">TogetherServer could not open its local data. Another instance may be starting.</div><p>Keep the existing instance available. Do not replace or clear its data.</p>`,
        'data-rejected':`<div class="ts-alert" data-tone="error">TogetherServer could not safely open its local data.</div><p>No server action was started. Review the data or schema problem before restarting.</p>`,
        'local-gui-failed':`<div class="ts-alert" data-tone="error">TogetherServer could not start its local GUI.</div><p>Review the startup problem. This message does not establish whether a previously managed server is running.</p>`,
        'link-failed':`<div class="ts-alert" data-tone="error">Could not open the selected page.</div><p>The requested external page did not open. The application stays available.</p>`
      },
      footer:`<button type="button" class="ts-button" data-close>OK</button>`
    },
    {
      id:'native-runtime-fallback', title:'The local interface is unavailable', area:'app',
      description:'Native fallback shown before the bundled UI can render.',
      states:['normal','missing-runtime','navigation-failed','render-timeout'],
      html:`<section class="ts-panel"><h3>Microsoft Edge WebView2 Runtime is required</h3><p>Install the Windows runtime, then reopen TogetherServer.</p><p class="ts-muted">The owner chooses whether to open the approved Microsoft download page.</p></section>`,
      variants:{
        'missing-runtime':`<section class="ts-panel"><h3>Microsoft Edge WebView2 Runtime is unavailable</h3><p>Review the required runtime before retrying the local interface.</p></section>`,
        'navigation-failed':`<div class="ts-alert" data-tone="error">TogetherServer could not load its local interface. Close the app and try again.</div>`,
        'render-timeout':`<div class="ts-alert" data-tone="error">The local interface did not finish rendering.</div><p>Close the app and review the startup problem before retrying.</p>`
      },
      footer:`<button type="button" class="ts-button" data-close>Close preview</button><button type="button" class="ts-button" data-action="refresh">Review runtime download</button>`
    },
    {
      id:'native-close-warning', title:'TogetherServer is still open', area:'app',
      description:'A native close safeguard keeps the application available.',
      states:['normal','draft-save-failed','managed-server-active','close-failed'],
      html:`<div class="ts-alert" data-tone="warning">Unfinished edits could not be confirmed as protected drafts.</div><p>Review the draft message, retry saving, or deliberately discard it before quitting.</p>`,
      variants:{
        'draft-save-failed':`<div class="ts-alert" data-tone="warning">Unfinished edits could not be confirmed as protected drafts.</div><p>Keep the app open. Review the draft, retry saving, or deliberately discard it.</p>`,
        'managed-server-active':`<div class="ts-alert" data-tone="warning">A managed server is active or needs process review.</div><p>Use Host to stop it gracefully or resolve its recorded identity before quitting.</p>`,
        'close-failed':`<div class="ts-alert" data-tone="error">TogetherServer could not close yet.</div><p>The app remains open. Review the current problem before trying Quit again.</p>`
      },
      footer:`<button type="button" class="ts-button" data-close>Keep app open</button>`
    }
  ],
  coverage:[
    {source:'src/TogetherServer/DesktopLaunch.cs:61',label:'Native startup message fallback',kind:'dialog',target:'native-startup-error',decision:'retained',note:'Native fallback remains available when the WebView cannot show an app dialog.'},
    {source:'src/TogetherServer/DesktopWindow.cs:473',label:'Window startup failed',kind:'dialog',target:'native-startup-error',decision:'retained',note:'Window-failed state preserves the outcome.'},
    {source:'src/TogetherServer/Program.cs:56',label:'Local data unavailable at startup',kind:'dialog',target:'native-startup-error',decision:'retained',note:'Separate from invalid data; no destructive recovery.'},
    {source:'src/TogetherServer/Program.cs:65',label:'Local data rejected at startup',kind:'dialog',target:'native-startup-error',decision:'retained',note:'No server action was started.'},
    {source:'src/TogetherServer/Program.cs:2352',label:'Local GUI startup failed',kind:'dialog',target:'native-startup-error',decision:'retained',note:'The interface failure supplies no process authority.'},
    {source:'src/TogetherServer/DesktopWindow.cs:910',label:'WebView2 download-page dispatch failed',kind:'dialog',target:'native-startup-error',decision:'retained',note:'Atlas does not dispatch a browser or install a runtime.'},
    {source:'src/TogetherServer/DesktopWindow.cs:920',label:'Approved external page dispatch failed',kind:'dialog',target:'native-startup-error',decision:'retained',note:'Link-failed state retains the local app.'},
    {source:'src/TogetherServer/DesktopWindow.cs:665',label:'Missing WebView2 runtime fallback',kind:'flow',target:'native-runtime-fallback',decision:'retained',note:'Native fallback before the bundled interface is available.'},
    {source:'src/TogetherServer/DesktopWindow.cs:714',label:'Local interface navigation failure',kind:'flow',target:'native-runtime-fallback',decision:'retained',note:'Explicit navigation-failed state.'},
    {source:'src/TogetherServer/DesktopWindow.cs:974',label:'Native Quit refused because draft protection failed',kind:'dialog',target:'native-close-warning',decision:'retained',note:'Draft-save-failed state keeps the app open.'},
    {source:'src/TogetherServer/DesktopWindow.cs:992',label:'Native Quit refused by managed-server gate',kind:'dialog',target:'native-close-warning',decision:'retained',note:'Managed-server-active state keeps the app open.'},
    {source:'src/TogetherServer/DesktopWindow.cs:1000',label:'Native Quit request failed',kind:'dialog',target:'native-close-warning',decision:'retained',note:'Close-failed state retains the running app.'}
  ]
};
