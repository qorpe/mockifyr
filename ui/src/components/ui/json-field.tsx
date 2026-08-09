import { useTranslation } from 'react-i18next'
import { JsonEditor, JsonField as KitJsonField, type JsonFieldLabels } from '@qorpe/ui/json-editor'

// The kit's JSON editor with this app's i18n bound ONCE (the kit is framework-free —
// strings arrive as props). Not a fork: rendering, theme and behavior are the kit's.
export { JsonEditor }

export function JsonField(props: Omit<React.ComponentProps<typeof KitJsonField>, 'labels'>) {
  const { t } = useTranslation()
  const labels: JsonFieldLabels = {
    upload: t('editor.upload'),
    beautify: t('editor.beautify'),
    copy: t('editor.copy'),
    copied: t('editor.copied'),
  }
  return <KitJsonField {...props} labels={labels} />
}
