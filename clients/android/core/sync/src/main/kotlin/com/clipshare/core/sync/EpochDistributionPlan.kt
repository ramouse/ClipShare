package com.clipshare.core.sync

import com.clipshare.core.vault.DeviceId

data class PairedDeviceState(val deviceId: DeviceId, val revoked: Boolean)

class EpochDistributionPlan private constructor(
    val previousEpoch: Long,
    val newEpoch: Long,
    val recipients: Set<DeviceId>,
) {
    fun validateGeneratedWrappers(wrapperRecipients: Set<DeviceId>) {
        require(wrapperRecipients == recipients) {
            "A new epoch must be wrapped exactly for the non-revoked device set."
        }
    }

    companion object {
        fun create(previousEpoch: Long, devices: Collection<PairedDeviceState>): EpochDistributionPlan {
            require(previousEpoch > 0 && previousEpoch < Long.MAX_VALUE) { "The previous epoch must be rotatable." }
            require(devices.map(PairedDeviceState::deviceId).toSet().size == devices.size) {
                "Paired devices must be unique."
            }
            return EpochDistributionPlan(
                previousEpoch,
                previousEpoch + 1,
                devices.filterNot(PairedDeviceState::revoked).mapTo(mutableSetOf(), PairedDeviceState::deviceId),
            )
        }
    }
}
