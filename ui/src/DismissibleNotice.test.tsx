import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { DismissibleNotice } from './DismissibleNotice'
import { ActionFeedback } from './QolWidgets'
import noticeCss from './DismissibleNotice.css?inline'
import companionCss from './companion.css?inline'
import qolCss from './qol.css?inline'
import coreCss from './core.css?inline'

describe('shared alert dismissal', () => {
  it('anchors close controls through the existing banner and compact Attention CSS cascade', () => {
    document.documentElement.setAttribute('data-density', 'compact')
    try {
      render(<><style>{[noticeCss, companionCss, qolCss, coreCss].join('\n')}</style><div className="shell app-shell">
        <DismissibleNotice as="aside" className="update-banner" role="status">App update available</DismissibleNotice>
        <div className="attention-workspace"><div className="attention-list"><DismissibleNotice className="notification-item" role="alert">Connection issue</DismissibleNotice></div></div>
        <DismissibleNotice as="span" className="server-chat-sync-feedback" role="status">Chat refreshed</DismissibleNotice>
      </div></>)
      for (const text of ['App update available', 'Connection issue']) {
        const notice = screen.getByText(text).closest('.dismissible-notice') as HTMLElement
        expect(getComputedStyle(notice).position).toBe('relative')
        expect(getComputedStyle(notice).paddingInlineEnd).toBe('3.5rem')
      }
      expect(getComputedStyle(screen.getByText('Chat refreshed')).display).toBe('inline-flex')
    } finally { document.documentElement.removeAttribute('data-density') }
  })

  it('closes presentation without invoking an action and stays closed on identical polling', () => {
    const action = vi.fn()
    const dismissed = vi.fn()
    const notice = () => <DismissibleNotice role="alert" scope="saved-server" onDismiss={dismissed}>
      Connection unavailable.<button onClick={action}>Retry connection</button>
    </DismissibleNotice>
    const view = render(notice())
    const close = screen.getByRole('button', { name: 'Dismiss notification' })
    expect(close.parentElement).toBe(screen.getByRole('alert'))
    fireEvent.click(close)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    view.rerender(notice())
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(dismissed).toHaveBeenCalledOnce()
    expect(action).not.toHaveBeenCalled()
  })

  it('shows a changed message or different saved-server scope', () => {
    const view = render(<DismissibleNotice as="p" role="alert" scope="one">Unavailable</DismissibleNotice>)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
    view.rerender(<DismissibleNotice as="p" role="alert" scope="two">Unavailable</DismissibleNotice>)
    expect(screen.getByRole('alert')).toHaveTextContent('Unavailable')
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
    view.rerender(<DismissibleNotice as="p" role="alert" scope="two">Permission denied</DismissibleNotice>)
    expect(screen.getByRole('alert')).toHaveTextContent('Permission denied')
  })

  it('keeps an ongoing operation dismissed through progress updates and shows a fresh result', () => {
    const view = render(<DismissibleNotice role="status" noticeKey="operation:pending" trackMessage={false} aria-busy>Downloading 10%</DismissibleNotice>)
    expect(screen.getByRole('button', { name: 'Dismiss notification' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
    view.rerender(<DismissibleNotice role="status" noticeKey="operation:pending" trackMessage={false} aria-busy>Downloading 80%</DismissibleNotice>)
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    view.rerender(<DismissibleNotice role="status" noticeKey="operation:failed">Download failed</DismissibleNotice>)
    expect(screen.getByRole('status')).toHaveTextContent('Download failed')
  })

  it('shows a new action receipt even when its result text repeats', () => {
    const view = render(<ActionFeedback notice={{ good: false, text: 'Save failed', receivedAtMs: 1 }} />)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss result' }))
    view.rerender(<ActionFeedback notice={{ good: false, text: 'Save failed', receivedAtMs: 1 }} />)
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    view.rerender(<ActionFeedback notice={{ good: false, text: 'Save failed', receivedAtMs: 2 }} />)
    expect(screen.getByRole('status')).toHaveTextContent('Save failed')
  })

  it('shows changed warning text under the same signed record or event', () => {
    const view = render(<DismissibleNotice role="alert" noticeKey="signed-record">Host is unavailable</DismissibleNotice>)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
    view.rerender(<DismissibleNotice role="alert" noticeKey="signed-record">Host identity needs review</DismissibleNotice>)
    expect(screen.getByRole('alert')).toHaveTextContent('Host identity needs review')
  })

  it('moves keyboard focus to the next control when a focused close button disappears', () => {
    render(<section><DismissibleNotice role="alert">An issue occurred</DismissibleNotice><button>Retry</button></section>)
    const close = screen.getByRole('button', { name: 'Dismiss notification' })
    close.focus()
    fireEvent.click(close)
    expect(screen.getByRole('button', { name: 'Retry' })).toHaveFocus()
  })
})
