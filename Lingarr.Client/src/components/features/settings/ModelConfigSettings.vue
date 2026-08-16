<template>
    <CardComponent title="Model Configuration">
        <template #description>
            Fine-tune model parameters for translation requests. These apply to AI-powered services.
        </template>
        <template #content>
            <SaveNotification ref="saveNotification" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Temperature:</span>
                Randomness (0.0–2.0). Lower = more deterministic translations.
            </div>
            <InputComponent
                v-model="temperature"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                placeholder="0.6"
                @update:validation="(val) => (isValid.temperature = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Top P:</span>
                Nucleus sampling (0.0–1.0). Leave empty to use model default.
            </div>
            <InputComponent
                v-model="topP"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                placeholder=""
                @update:validation="(val) => (isValid.topP = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Max Tokens:</span>
                Maximum tokens in the response. Leave empty for model default.
            </div>
            <InputComponent
                v-model="maxTokens"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                placeholder=""
                @update:validation="(val) => (isValid.maxTokens = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Reasoning Budget:</span>
                For models with reasoning (e.g. DeepSeek R1). Leave empty to disable.
            </div>
            <InputComponent
                v-model="reasoningBudget"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                placeholder=""
                @update:validation="(val) => (isValid.reasoningBudget = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Chat Template Kwargs:</span>
                Extra kwargs passed to the chat template (JSON format). Leave empty to disable.
            </div>
            <InputComponent
                v-model="chatTemplateKwargs"
                placeholder=""
                @update:validation="(val) => (isValid.chatTemplateKwargs = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Reasoning Effort:</span>
                Reasoning effort level (e.g. 'low', 'medium', 'high'). Leave empty to disable.
            </div>
            <InputComponent
                v-model="reasoningEffort"
                placeholder=""
                @update:validation="(val) => (isValid.reasoningEffort = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Structured Output:</span>
                Enable structured JSON output for batch translations. Disable if your model does not
                support it (avoids a failed request on every batch).
            </div>
            <ToggleButton v-model="structuredOutput">
                <span class="text-sm font-medium text-primary-content">
                    {{ structuredOutput == 'true' ? 'Enabled' : 'Disabled' }}
                </span>
            </ToggleButton>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref, reactive } from 'vue'
import { useSettingStore } from '@/store/setting'
import { INPUT_VALIDATION_TYPE, SETTINGS } from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'

const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const settingsStore = useSettingStore()
const isValid = reactive({
    temperature: true,
    topP: true,
    maxTokens: true,
    reasoningBudget: true,
    chatTemplateKwargs: true,
    reasoningEffort: true
})

const temperature = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MODEL_TEMPERATURE) as string ?? '',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MODEL_TEMPERATURE, newValue, isValid.temperature)
        saveNotification.value?.show()
    }
})

const topP = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MODEL_TOP_P) as string ?? '',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MODEL_TOP_P, newValue, isValid.topP)
        saveNotification.value?.show()
    }
})

const maxTokens = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MODEL_MAX_TOKENS) as string ?? '',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MODEL_MAX_TOKENS, newValue, isValid.maxTokens)
        saveNotification.value?.show()
    }
})

const reasoningBudget = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MODEL_REASONING_BUDGET) as string ?? '',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MODEL_REASONING_BUDGET, newValue, isValid.reasoningBudget)
        saveNotification.value?.show()
    }
})

const chatTemplateKwargs = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MODEL_CHAT_TEMPLATE_KWARGS) as string ?? '',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MODEL_CHAT_TEMPLATE_KWARGS, newValue, isValid.chatTemplateKwargs)
        saveNotification.value?.show()
    }
})

const reasoningEffort = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MODEL_REASONING_EFFORT) as string ?? '',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MODEL_REASONING_EFFORT, newValue, isValid.reasoningEffort)
        saveNotification.value?.show()
    }
})

const structuredOutput = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MODEL_STRUCTURED_OUTPUT) as string ?? 'false',
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MODEL_STRUCTURED_OUTPUT, newValue, true)
        saveNotification.value?.show()
    }
})
</script>
