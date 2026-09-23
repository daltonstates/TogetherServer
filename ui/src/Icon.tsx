import type { ReactNode } from 'react'

export type IconName = 'play' | 'stop' | 'copy' | 'invite' | 'refresh' | 'server' |
  'game' | 'plug' | 'check' | 'warning' | 'settings' | 'search' | 'link' |
  'eye' | 'eyeOff' | 'bell' | 'loader'

const paths: Record<IconName, ReactNode> = {
  play: <path d="m6 4 8 6-8 6V4Z" />,
  stop: <rect x="5" y="5" width="10" height="10" rx="1.5" />,
  copy: <><rect x="7" y="7" width="9" height="9" rx="2" /><path d="M13 7V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h1" /></>,
  invite: <><circle cx="8" cy="7" r="3" /><path d="M3.5 16c.5-3 2.1-4.5 4.5-4.5s4 1.5 4.5 4.5M15 6v6M12 9h6" /></>,
  refresh: <><path d="M16 6V3l-2 2a7 7 0 1 0 1.8 7" /><path d="M16 3h-4" /></>,
  server: <><rect x="3" y="3" width="14" height="5" rx="2" /><rect x="3" y="12" width="14" height="5" rx="2" /><path d="M6 5.5h.01M6 14.5h.01M9 5.5h5M9 14.5h5" /></>,
  game: <><path d="M6 7h8a4 4 0 0 1 3.7 5.5l-1 2.3a2 2 0 0 1-3.2.7L12 14h-4l-1.5 1.5a2 2 0 0 1-3.2-.7l-1-2.3A4 4 0 0 1 6 7Z" /><path d="M7 9v4M5 11h4M14.5 10.5h.01M16 12h.01" /></>,
  plug: <><path d="M7 3v5M13 3v5M5 8h10v1a5 5 0 0 1-10 0V8ZM10 14v3" /></>,
  check: <path d="m4 10 4 4 8-9" />,
  warning: <><path d="M10 3 2.8 16h14.4L10 3Z" /><path d="M10 8v3M10 14h.01" /></>,
  settings: <><circle cx="10" cy="10" r="2.5" /><path d="M16.2 11.5a6.5 6.5 0 0 0 0-3l1.3-1-1.8-3-1.6.7a6.5 6.5 0 0 0-2.6-1.5L11.3 2H8.7l-.2 1.7a6.5 6.5 0 0 0-2.6 1.5l-1.6-.7-1.8 3 1.3 1a6.5 6.5 0 0 0 0 3l-1.3 1 1.8 3 1.6-.7a6.5 6.5 0 0 0 2.6 1.5l.2 1.7h2.6l.2-1.7a6.5 6.5 0 0 0 2.6-1.5l1.6.7 1.8-3-1.3-1Z" /></>,
  search: <><circle cx="9" cy="9" r="5" /><path d="m13 13 4 4" /></>,
  link: <><path d="M8 12 6.5 13.5a3 3 0 0 1-4-4L5 7a3 3 0 0 1 4 0" /><path d="m12 8 1.5-1.5a3 3 0 0 1 4 4L15 13a3 3 0 0 1-4 0M7 10h6" /></>,
  eye: <><path d="M2 10s3-5 8-5 8 5 8 5-3 5-8 5-8-5-8-5Z" /><circle cx="10" cy="10" r="2.2" /></>,
  eyeOff: <><path d="M3 3l14 14M8.4 5.2A8 8 0 0 1 10 5c5 0 8 5 8 5a13 13 0 0 1-2.2 2.8M12.6 14.6A8 8 0 0 1 10 15c-5 0-8-5-8-5a13 13 0 0 1 2.4-3" /><path d="M8.6 8.6a2 2 0 0 0 2.8 2.8" /></>,
  bell: <><path d="M5 8a5 5 0 0 1 10 0c0 5 2 5 2 6H3c0-1 2-1 2-6Z" /><path d="M8 17h4" /></>,
  loader: <><circle cx="10" cy="10" r="7" opacity=".3" /><path d="M10 3a7 7 0 0 1 7 7" /></>
}

export function Icon({ name, size = 17 }: { name: IconName; size?: number }) {
  return <svg className={name === 'loader' ? 'icon icon-spin' : 'icon'} aria-hidden="true" viewBox="0 0 20 20" width={size} height={size}
    fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
    {paths[name]}
  </svg>
}
