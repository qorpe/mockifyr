{{- define "mockifyr.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "mockifyr.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "mockifyr.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "mockifyr.labels" -}}
app.kubernetes.io/name: {{ include "mockifyr.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "mockifyr.selectorLabels" -}}
app.kubernetes.io/name: {{ include "mockifyr.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- /* The Secret holding admin credentials: an existing one when named, else the chart's own. */ -}}
{{- define "mockifyr.adminSecretName" -}}
{{- if .Values.adminAuth.existingSecret -}}
{{- .Values.adminAuth.existingSecret -}}
{{- else -}}
{{- printf "%s-admin" (include "mockifyr.fullname" .) -}}
{{- end -}}
{{- end -}}

{{- define "mockifyr.cryptoSecretName" -}}
{{- if .Values.cryptography.existingSecret -}}
{{- .Values.cryptography.existingSecret -}}
{{- else -}}
{{- printf "%s-crypto" (include "mockifyr.fullname" .) -}}
{{- end -}}
{{- end -}}
