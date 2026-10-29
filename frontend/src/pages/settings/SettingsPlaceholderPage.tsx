import { NotDesigned } from '../../components/NotDesigned'
import { SettingsFrame } from './SettingsFrame'

export function SettingsPlaceholderPage({ title }: { title: string }) {
  return (
    <SettingsFrame>
      <NotDesigned title={title} description={`The ${title} settings screen is not in the design yet.`} />
    </SettingsFrame>
  )
}
