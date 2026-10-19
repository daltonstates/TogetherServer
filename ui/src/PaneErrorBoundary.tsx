import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'

type Props = {
  children: ReactNode
  title: string
  resetKey?: string | number
}

type State = { failed: boolean; attempt: number }

export class PaneErrorBoundary extends Component<Props, State> {
  state: State = { failed: false, attempt: 0 }

  static getDerivedStateFromError(): Partial<State> {
    return { failed: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`TogetherServer ${this.props.title} pane failed.`, error, info.componentStack)
  }

  componentDidUpdate(previous: Props) {
    if (this.state.failed && previous.resetKey !== this.props.resetKey)
      this.setState({ failed: false })
  }

  render() {
    if (!this.state.failed) return this.props.children
    return <section className="pane-error">
      <DismissibleNotice role="alert" scope={String(this.props.resetKey ?? this.props.title)} noticeKey={this.state.attempt} dismissLabel="Dismiss section error">
      <strong>{this.props.title} could not be shown</strong>
      <p>This section could not load. Try again. Your server has not changed.</p>
      </DismissibleNotice>
      <Button className="secondary" onClick={() => this.setState(current => ({ failed: false, attempt: current.attempt + 1 }))}>Try again</Button>
    </section>
  }
}
