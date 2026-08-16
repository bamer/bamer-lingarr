import { acceptHMRUpdate, defineStore } from 'pinia'
import {
    IActiveTranslation,
    IFilter,
    IPagedResult,
    IProofreadLineApplyRequest,
    IProofreadStatus,
    IRequestProgress,
    ITranslationRequest,
    IUseTranslationRequestStore,
    TRANSLATION_STATUS
} from '@/ts'
import services from '@/services'

export const useTranslationRequestStore = defineStore('translateRequest', {
    state: (): IUseTranslationRequestStore => ({
        activeTranslations: [],
        translationRequests: {
            totalCount: 0,
            pageSize: 0,
            pageNumber: 0,
            items: []
        },
        filter: {
            searchQuery: '',
            sortBy: 'CreatedAt',
            isAscending: true,
            pageNumber: 1
        },
        selectedRequests: [] as ITranslationRequest[],
        selectAll: false,
        proofreadSupported: false
    }),
    getters: {
        getActiveTranslationCount: (state: IUseTranslationRequestStore): number =>
            state.activeTranslations.length,
        getTranslationRequests(): IPagedResult<ITranslationRequest> {
            return this.translationRequests
        },
        getFilter: (state: IUseTranslationRequestStore): IFilter => state.filter,
        getSelectedRequests: (state: IUseTranslationRequestStore): ITranslationRequest[] =>
            state.selectedRequests
    },
    actions: {
        async setFilter(filterVal: IFilter) {
            this.filter = filterVal.searchQuery ? { ...filterVal, pageNumber: 1 } : filterVal
            await this.fetch()
        },
        async fetch() {
            // Preserve SignalR-updated progress values before fetch overwrites items
            const progressMap = new Map<number, number>()
            for (const item of this.translationRequests.items) {
                if (item.progress) {
                    progressMap.set(item.id, item.progress)
                }
            }
            this.translationRequests = await services.translationRequest.requests<
                IPagedResult<ITranslationRequest>
            >(
                this.filter.pageNumber,
                this.filter.searchQuery,
                this.filter.sortBy,
                this.filter.isAscending
            )
            // Restore progress values from SignalR updates
            for (const item of this.translationRequests.items) {
                const saved = progressMap.get(item.id)
                if (saved != null && item.progress === 0) {
                    item.progress = saved
                }
            }
        },
        setActiveTranslations(activeTranslations: IActiveTranslation[]) {
            this.activeTranslations = activeTranslations
        },
        async fetchActiveTranslations() {
            this.activeTranslations =
                await services.translationRequest.getActiveTranslations<IActiveTranslation[]>()
        },
        async cancel(translationRequest: ITranslationRequest) {
            await services.translationRequest.cancel<string>(translationRequest)
        },
        async remove(translationRequest: ITranslationRequest) {
            await services.translationRequest.remove<string>(translationRequest).finally(() => {
                this.translationRequests.items = this.translationRequests.items.filter(
                    (request) => request.id !== translationRequest.id
                )
            })
        },
        async retry(translationRequest: ITranslationRequest) {
            await services.translationRequest.retry<string>(translationRequest)
            await this.fetch()
        },
        async resume(translationRequest: ITranslationRequest) {
            await services.translationRequest.resume<string>(translationRequest)
            await this.fetch()
        },
        async proofread(translationRequest: ITranslationRequest) {
            await services.translationRequest.proofread<string>(translationRequest)
            await this.fetch()
        },
        async applyProofreadLine(request: IProofreadLineApplyRequest) {
            return await services.translationRequest.applyProofreadLine<string>(request)
        },
        async fetchProofreadStatus() {
            const status = await services.translate.proofreadStatus<IProofreadStatus>()
            this.proofreadSupported = status.supported
        },
        async updateProgress(requestProgress: IRequestProgress) {
            this.translationRequests.items = this.translationRequests.items.map(
                (request: ITranslationRequest) => {
                    if (request.id === requestProgress.id) {
                        return {
                            ...request,
                            status: requestProgress.status,
                            progress: requestProgress.progress,
                            completedAt: requestProgress.completedAt,
                            errorMessage: requestProgress.errorMessage ?? request.errorMessage,
                            stackTrace: requestProgress.stackTrace ?? request.stackTrace
                        }
                    }
                    return request
                }
            )
        },
        clearSelection() {
            this.selectedRequests = []
            this.selectAll = false
        },
        toggleSelectAll() {
            this.selectAll = !this.selectAll
            if (this.selectAll) {
                this.selectedRequests = [...this.translationRequests.items]
            } else {
                this.selectedRequests = []
            }
        },

        toggleSelect(request: ITranslationRequest) {
            const index = this.selectedRequests.findIndex((r) => r.id === request.id)
            if (index === -1) {
                this.selectedRequests.push(request)
            } else {
                this.selectedRequests.splice(index, 1)
            }
            this.selectAll = this.selectedRequests.length === this.translationRequests.items.length
        },
        async removeCompleted() {
            const completed = this.translationRequests.items.filter(
                (r) => r.status === TRANSLATION_STATUS.COMPLETED
            )
            for (const request of completed) {
                await services.translationRequest.remove<string>(request)
            }
            await this.fetch()
        },
        async retryFailed() {
            await services.translationRequest.retryAllFailed<number>()
            await this.fetch()
        },
        async resumeAllFailed() {
            await services.translationRequest.resumeAllFailed<number>()
            await this.fetch()
        }
    }
})
export default useTranslationRequestStore

if (import.meta.hot) {
    import.meta.hot.accept(acceptHMRUpdate(useTranslationRequestStore, import.meta.hot))
}
