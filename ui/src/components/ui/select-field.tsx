import { Controller, type Control, type FieldPath, type FieldValues } from 'react-hook-form'
import { Select, type SelectOption } from '@qorpe/ui'

/**
 * The kit Select under react-hook-form (the kit's own D4 pattern, proven in its tests):
 * `register()` spreads onto native elements, but the family Select is controlled, so a
 * Controller carries value/onChange. Everything visual — the listbox, the keyboard walk,
 * disabled options, the viewport flip — is the kit's; this file is only the bridge.
 */
export function SelectField<T extends FieldValues>({
  control,
  name,
  options,
  label,
  className,
}: {
  control: Control<T>
  name: FieldPath<T>
  options: SelectOption[]
  /** The accessible name — the kit refuses a nameless select, and so should we. */
  label: string
  className?: string
}) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field }) => (
        <Select
          aria-label={label}
          value={(field.value as string) ?? ''}
          onChange={field.onChange}
          options={options}
          className={className}
        />
      )}
    />
  )
}

/** Plain string list → the kit's option shape (the common case: values ARE the labels). */
export const selectOptions = (values: readonly string[]): SelectOption[] => values.map((value) => ({ value }))
