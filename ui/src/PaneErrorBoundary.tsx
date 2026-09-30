import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Button } from './Controls'

type Props = {
  children: ReactNode
  title: string
  resetKey?: string | number
}

type State = { failed: boolean }

export class PaneErrorBoundary extends Component<Props, State> {
  state: State = { failed: false }

  static getDerivedStateFromError(): State {
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
    return <section className="pane-error" role="alert">
      <strong>{this.props.title} could not be shown</strong>
      <p>The rest of TogetherServer is still available. Try this section again; no server action was taken.</p>
      <Button className="secondary" onClick={() => this.setState({ failed: false })}>Try section again</Button>
    </section>
  }
}
