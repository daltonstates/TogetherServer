import { Button, Select } from './Controls'
import { Icon } from './Icon'
import type { SectionItem } from './workspaceSections'

export function SectionSidebar<T extends string>({ title, label, sections, section, onSelect }: {
  title: string; label: string; sections: SectionItem<T>[]; section?: T; onSelect: (section: T) => void
}) {
  return <div className="section-sidebar-group">
    <h2 className="section-sidebar-title">{title}</h2>
    <label className="section-mobile-picker"><span className="sr-only">Section</span>
      <Select aria-label={label} value={section ?? ''} onChange={event => onSelect(event.target.value as T)}>
        {!section && <option value="" disabled>Choose a section</option>}
        {sections.map(item => <option key={item.id} value={item.id}>{item.label}</option>)}
      </Select>
    </label>
    <nav className="section-sidebar-links" aria-label={label}>
      {sections.map(item => <Button key={item.id} className={section === item.id ? 'selected' : 'secondary'}
        aria-current={section === item.id ? 'page' : undefined} onClick={() => onSelect(item.id)}>
        <Icon name={item.icon} /><span>{item.label}</span>
      </Button>)}
    </nav>
  </div>
}
