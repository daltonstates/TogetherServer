import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Button } from './Controls'

type State = { failed: boolean }

export class AppErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { failed: false }

  static getDerivedStateFromError(): State {
    return { failed: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('TogetherServer interface render failed.', error, info.componentStack)
  }

  render() {
    if (!this.state.failed) return this.props.children
    return <div className="shell"><main><section className="panel fatal-error" role="alert">
      <h1>The interface needs to reload</h1>
      <p>Your local service and any managed game process continue separately. Reload this window to reconnect to them.</p>
      <Button onClick={() => window.location.reload()}>Reload interface</Button>
    </section></main></div>
  }
}
