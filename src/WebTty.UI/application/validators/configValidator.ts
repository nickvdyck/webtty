import { AppConfig } from "../interfaces"

const configValidator = (config: Partial<AppConfig>): config is AppConfig => {
    if (config.ttyHost === undefined) {
        throw new Error("AppConfig ttyHost is not defined")
    }

    if (config.ttyPath === undefined) {
        throw new Error("AppConfig ttyPath is not defined")
    }

    if (config.theme === undefined) {
        throw new Error("AppConfig theme is not defined")
    }

    return true
}

export default configValidator
